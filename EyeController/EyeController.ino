/*
 * BPPV Robotic Eye Simulation
 * MPU6050 Head Tracker + PCA9685 Motor Driver
 *
 * Right eye: CH0=horizontal, CH1=vertical, CH2=torsion
 * Left eye:  CH4=horizontal, CH5=vertical, CH6=torsion
 *
 * Both eyes move with mirror effect - horizontal and torsion signs flip
 * N or NEUTRAL -> all motors neutral
 *
 * File layout:
 *   1. Declarations (structs, config constants, global state) - unchanged
 *      order throughout, since Arduino auto-generates function prototypes
 *      and inserts them immediately before the first function in the file;
 *      any struct used in a function signature must therefore stay up here,
 *      above every function, or the sketch fails to compile.
 *   2. SECTION 1 - BPPV CORE: everything needed to actually run a clinical
 *      exam (motor control, calibration, state machine, head tracking,
 *      core serial commands).
 *   3. SECTION 2 - DATA COLLECTION FOR EVALUATION: code that exists purely
 *      to gather the data behind the dissertation's evaluation section
 *      (positional-sync calibration test mode, IMU jitter/noise trial).
 *      None of this runs during a normal triggered exam.
 *   4. setup() / loop()
 */

#include <Wire.h>
#include <I2Cdev.h>
#include <MPU6050_6Axis_MotionApps20.h>
#include <Adafruit_PWMServoDriver.h>
#include <string.h>
#include <stdlib.h>

// Calibration lookup-table entry (physical eye angle -> digital command
// that actually achieves it). Declared here, immediately after the
// includes and before any function, because compensate() takes a
// CalPoint* parameter and Arduino's auto-generated prototype for it must
// be able to see this type.
struct CalPoint { float physical; float digital; };

// **********************************************
// DEV/DEBUG TOGGLE
// Lets the sketch run and auto-trigger a BPPV sequence without a
// connected IMU, for bench testing the motors alone.
// **********************************************
#define SKIP_IMU_TEST   false
#define BPPV_SIDE       'R'

// **********************************************
// IMU (MPU6050 + DMP)
// **********************************************
MPU6050 mpu;
bool dmpReady = false;
uint8_t devStatus;
uint16_t packetSize;
uint8_t fifoBuffer[64];
Quaternion q;
VectorFloat gravity;
float yawOffset = 0, pitchOffset = 0, rollOffset = 0;
const unsigned long stabilizationDelay = 15000;
const int calibrationSamples = 100;
float imuYaw = 0, imuPitch = 0, imuRoll = 0;   // Kalman-filtered, broadcast to Unity
float tiltFromVertical = 0;

struct EulerAngles {
    float pitch;
    float roll;
    float yaw;
    float tilt;
};

// **********************************************
// PCA9685 / servo output
// **********************************************
Adafruit_PWMServoDriver pca = Adafruit_PWMServoDriver(0x40);

#define PWM_FREQ      50
#define PULSE_MIN     205
#define PULSE_MAX     410
#define PULSE_NEUTRAL 307

#define R_CH_HORIZONTAL  0
#define R_CH_VERTICAL    1
#define R_CH_TORSION     2

#define L_CH_HORIZONTAL  4
#define L_CH_VERTICAL    5
#define L_CH_TORSION     6

#define SERVO_NEUTRAL 90
#define SERVO_MIN     12
#define SERVO_MAX     168

// Vertical uses one gain both directions (no asymmetry found for this
// axis). Torsion/horizontal use separate positive/negative gains, defined
// with the rest of the calibration machinery further down.
#define GAIN_VERTICAL   3.5

// **********************************************
// BPPV clinical parameters (exam timing + target angles)
// **********************************************
#define PEAK_TORSION_DEG     19.0
#define PEAK_VERTICAL_DEG    18.0
#define PEAK_HORIZONTAL_DEG  6.0

#define LATENCY_MS      3000
#define CRESCENDO_MS    4000
#define NYSTAGMUS_MS    10000
#define DECRESCENDO_MS  8000
#define REVERSAL_MS     6000

#define BEAT_DRIFT_DEG        14.0
#define BEAT_DRIFT_SPEED_TOR  22.0
#define BEAT_DRIFT_SPEED_VER  14.0
#define BEAT_DRIFT_SPEED_HOR   5.5
#define BEAT_SNAP_SPEED      120.0

#define LATENCY_DRIFT_DEG         1.5
#define CRESCENDO_TARGET_TORSION  16.0
#define CRESCENDO_TARGET_VERTICAL 14.0
#define TAU_DECAY                 12.0
#define LOOP_INTERVAL_MS          10

// **********************************************
// Motor / exam state
// **********************************************
float eyeTorsion    = 0.0;
float eyeVertical   = 0.0;
float eyeHorizontal = 0.0;

float headYaw   = 0;
float headPitch = 0;
float headRoll  = 0;

enum BeatState { BEAT_DRIFT, BEAT_SNAP };
BeatState beatState = BEAT_DRIFT;

// motorDir: +1 = right posterior BPPV, -1 = left posterior BPPV
int motorDir = 1;

// activeEye: 'R' = right eye channels active, 'L' = left eye channels active
char activeEye = 'R';

int  currentPhase  = 0;
bool motorRunning  = false;
unsigned long phaseStartTime = 0;
unsigned long lastMotorStep  = 0;

// True while loop() is holding eyeTorsion/eyeVertical/eyeHorizontal at a
// fixed value set by a "C:" command (see SECTION 2), instead of running
// the exam state machine. Declared here since both the core loop() and
// the Section 2 command handler need it.
bool testModeActive = false;

// Fixed-size serial receive buffer (no heap allocation / fragmentation
// risk from using String on a memory-constrained board). Shared by every
// serial command, core and data-collection alike.
#define SERIAL_BUF_SIZE 21
char serialBuffer[SERIAL_BUF_SIZE] = "";
uint8_t serialBufferLen = 0;

// Simple single-axis Kalman filter used to smooth the raw DMP output
// before it's broadcast to Unity. Operational (runs every loop), not
// evaluation-only.
struct Kalman1D {
    float process_noise = 0.001;   // process noise
    float measured_noise = 0.05;   // measurement noise
    float error_estimate = 1.0;    // estimate error covariance
    float current_estimate = 0.0;  // current estimate

    float update(float measured_value) {
        error_estimate += process_noise;  // prediction step: uncertainty grows each step
        float kalman_gain = error_estimate / (error_estimate + measured_noise); // trust measurement vs. prediction
        current_estimate += kalman_gain * (measured_value - current_estimate);  // update estimate
        error_estimate *= (1.0 - kalman_gain); // update error covariance
        return current_estimate;
    }
};

Kalman1D kalYaw, kalPitch, kalRoll;

// Running mean/variance accumulator (Welford's algorithm) used only by
// the IMU jitter trial in SECTION 2, to characterise head-tracking noise
// for the dissertation's evaluation section. Declared up here (not next
// to the functions that use it) because printStat() takes a RunningStats&
// parameter, which is subject to the same prototype-ordering constraint
// as CalPoint above.
struct RunningStats {
    long n = 0;
    float mean = 0.0f;
    float M2 = 0.0f;

    void update(float x) {
        n++;
        float delta = x - mean;
        mean += delta / n;
        float delta2 = x - mean;
        M2 += delta * delta2;
    }

    float variance() { return n > 1 ? M2 / (n - 1) : 0.0f; }
    float stddev()   { return sqrt(variance()); }
    void reset()     { n = 0; mean = 0.0f; M2 = 0.0f; }
};

RunningStats rawYawStats, filtYawStats;
RunningStats rawPitchStats, filtPitchStats;
RunningStats rawRollStats, filtRollStats;
RunningStats tiltStats; // no filtered counterpart, tiltFromVertical is unfiltered by design

bool jitterTrialActive = false;
const unsigned long JITTER_SAMPLES = 300; // ~3s at ~100Hz DMP output


// ================================================================
// SECTION 1 - BPPV CORE
// Everything needed to run a real triggered exam: calibration,
// motor control, the clinical state machine, head tracking, and the
// core serial command set (R/L/N/RESET).
// ================================================================

// --- Positional-sync calibration tables ---
// Built from photographic measurement of achieved vs. commanded eye
// angle (see dissertation System Integration / Positional Synchronisation
// section). Maps a desired physical angle to the digital command that
// actually achieves it, correcting for firmware gain mismatch and
// non-linear mechanical response. Negative torsion and negative
// horizontal are not covered here: negative torsion beyond ~-12 deg is a
// servo torque ceiling (not a calibration problem, so not correctable by
// a table), and negative horizontal is passed through uncompensated
// below pending further work.
const CalPoint torsionCal[] = {
  {0.0, 0.0}, {4.45, 5.0}, {6.84, 10.0}, {11.38, 14.0}, {13.23, 17.0}, {16.61, 19.0}
};
const int torsionCalN = sizeof(torsionCal) / sizeof(CalPoint);

const CalPoint verticalCal[] = {
  {0.0, 0.0}, {7.53, 3.0}, {14.91, 6.0}, {24.48, 9.0}, {34.02, 12.0}, {42.89, 15.0}, {54.97, 18.0}
};
const int verticalCalN = sizeof(verticalCal) / sizeof(CalPoint);

const CalPoint horizPosCal[] = {
  {0.0, 0.0}, {0.88, 1.0}, {2.64, 2.0}, {4.41, 3.0}, {5.29, 4.0}, {5.29, 5.0}, {7.95, 6.0}
};
const int horizPosCalN = sizeof(horizPosCal) / sizeof(CalPoint);

// Piecewise-linear interpolation over a calibration table.
float compensate(const CalPoint* table, int n, float desiredPhysical) {
  if (desiredPhysical <= table[0].physical) return table[0].digital;
  if (desiredPhysical >= table[n - 1].physical) return table[n - 1].digital;
  for (int i = 0; i < n - 1; i++) {
    if (desiredPhysical >= table[i].physical && desiredPhysical <= table[i + 1].physical) {
      float span = table[i + 1].physical - table[i].physical;
      if (span < 0.001) return table[i].digital; // guards duplicate points, e.g. h4/h5
      float frac = (desiredPhysical - table[i].physical) / span;
      return table[i].digital + frac * (table[i + 1].digital - table[i].digital);
    }
  }
  return table[n - 1].digital;
}

// --- Motor utilities ---
uint16_t angleToPulse(float angle) {
    angle = constrain(angle, SERVO_MIN, SERVO_MAX);
    return (uint16_t)(PULSE_MIN + (angle / 180.0) * (PULSE_MAX - PULSE_MIN));
}

float eyeToServo(float eyeAngle, float gainPos, float gainNeg, float trim) {
    float gain = (eyeAngle >= 0.0f) ? gainPos : gainNeg;
    return constrain(SERVO_NEUTRAL + trim + (eyeAngle * gain), (float)SERVO_MIN, (float)SERVO_MAX);
}

// Per-motor trim, currently all zero. Kept as named constants (rather
// than removed) so a future bench-calibration pass has somewhere
// obvious to put a correction without touching writeAllMotors().
#define TRIM_R_TORSION 0.0f
#define TRIM_L_TORSION 0.0f
#define TRIM_R_VERTICAL 0.0f
#define TRIM_L_VERTICAL 0.0f
#define TRIM_R_HORIZONTAL 0.0f
#define TRIM_L_HORIZONTAL 0.0f

#define GAIN_TORSION_POS      4.0f
#define GAIN_TORSION_NEG      4.0f   // start here, tune by bench test
#define GAIN_HORIZONTAL_POS   3.0f
#define GAIN_HORIZONTAL_NEG   2.0f   // start here, tune by bench test

// **********************************************
// writeAllMotors
//
//   Vertical:   same sign (both eyes go up)
//   Horizontal: opposite sign (both eyes move toward nose = convergent)
//   Torsion:    opposite sign (both tops tilt same direction in space)
//
// Right BPPV: right eye = primary
// Left BPPV:  left eye = primary
//
// eyeTorsion/eyeVertical/eyeHorizontal are run through the calibration
// tables above before being converted to servo angles, so every other
// part of the code (state machine, clamping, broadcastState) keeps
// working in real physical/clinical degrees, unchanged.
// **********************************************
void writeAllMotors() {
    float tCmd = eyeTorsion >= 0 ?  compensate(torsionCal, torsionCalN,  eyeTorsion)
                                  : -compensate(torsionCal, torsionCalN, -eyeTorsion);
    float vCmd = eyeVertical >= 0 ? compensate(verticalCal, verticalCalN,  eyeVertical)
                                  : -compensate(verticalCal, verticalCalN, -eyeVertical);
    float hCmd = eyeHorizontal >= 0 ? compensate(horizPosCal, horizPosCalN, eyeHorizontal)
                                     : eyeHorizontal; // negative side not yet calibrated

    pca.setPWM(R_CH_TORSION, 0, angleToPulse(eyeToServo(-tCmd, GAIN_TORSION_POS, GAIN_TORSION_NEG, TRIM_R_TORSION)));
    pca.setPWM(R_CH_VERTICAL, 0, angleToPulse(eyeToServo(vCmd, GAIN_VERTICAL, GAIN_VERTICAL, TRIM_R_VERTICAL)));
    pca.setPWM(R_CH_HORIZONTAL, 0, angleToPulse(eyeToServo(-hCmd, GAIN_HORIZONTAL_POS, GAIN_HORIZONTAL_NEG, TRIM_R_HORIZONTAL)));

    pca.setPWM(L_CH_TORSION, 0, angleToPulse(eyeToServo( tCmd, GAIN_TORSION_POS, GAIN_TORSION_NEG, TRIM_L_TORSION)));
    pca.setPWM(L_CH_VERTICAL, 0, angleToPulse(eyeToServo( vCmd, GAIN_VERTICAL, GAIN_VERTICAL, TRIM_L_VERTICAL)));
    pca.setPWM(L_CH_HORIZONTAL, 0, angleToPulse(eyeToServo( hCmd, GAIN_HORIZONTAL_POS, GAIN_HORIZONTAL_NEG, TRIM_L_HORIZONTAL)));
}

void allNeutral() {
    pca.setPWM(R_CH_HORIZONTAL, 0, PULSE_NEUTRAL);
    pca.setPWM(R_CH_VERTICAL, 0, PULSE_NEUTRAL);
    pca.setPWM(R_CH_TORSION, 0, PULSE_NEUTRAL);
    pca.setPWM(L_CH_HORIZONTAL, 0, PULSE_NEUTRAL);
    pca.setPWM(L_CH_VERTICAL, 0, PULSE_NEUTRAL);
    pca.setPWM(L_CH_TORSION, 0, PULSE_NEUTRAL);
    eyeTorsion = 0.0;
    eyeVertical = 0.0;
    eyeHorizontal = 0.0;
    beatState = BEAT_DRIFT;
    currentPhase = 0;
    motorRunning = false;
    testModeActive = false; // any neutral/reset also leaves test-hold mode
    Serial.print(F("PHASE:0,")); Serial.println(millis());
}

// Constrains commanded angles to the clinical peak limits, and prints a
// CLAMPED line whenever a requested angle actually gets capped, so an
// out-of-range request is never silent.
void clampEyeAngles() {
    float reqT = eyeTorsion, reqV = eyeVertical, reqH = eyeHorizontal;
    eyeTorsion = constrain(eyeTorsion, -PEAK_TORSION_DEG, PEAK_TORSION_DEG);
    eyeVertical = constrain(eyeVertical, -PEAK_VERTICAL_DEG, PEAK_VERTICAL_DEG);
    eyeHorizontal = constrain(eyeHorizontal, -PEAK_HORIZONTAL_DEG, PEAK_HORIZONTAL_DEG);
    if (reqT != eyeTorsion) {
        Serial.print(F("CLAMPED torsion requested ")); Serial.print(reqT, 2);
        Serial.print(F(" applied ")); Serial.println(eyeTorsion, 2);
    }
    if (reqV != eyeVertical) {
        Serial.print(F("CLAMPED vertical requested ")); Serial.print(reqV, 2);
        Serial.print(F(" applied ")); Serial.println(eyeVertical, 2);
    }
    if (reqH != eyeHorizontal) {
        Serial.print(F("CLAMPED horizontal requested ")); Serial.print(reqH, 2);
        Serial.print(F(" applied ")); Serial.println(eyeHorizontal, 2);
    }
}

void advancePhase(unsigned long now) {
    currentPhase++;
    phaseStartTime = now;
    lastMotorStep = now;
    beatState = BEAT_DRIFT;
    Serial.print(F("PHASE:")); Serial.print(currentPhase);
    Serial.print(F(",")); Serial.println(now);
}

// **********************************************
// startBPPV
// Sets which eye is primary
// motorDir controls torsion and horizontal direction in state machine
// **********************************************
void startBPPV(char side) {
    allNeutral();
    activeEye = side;
    motorDir = (side == 'R') ? 1 : -1;
    motorRunning = true;
    currentPhase = 1;
    phaseStartTime = millis();
    lastMotorStep = millis();
    beatState = BEAT_DRIFT;
    Serial.print(F("PHASE:1,")); Serial.println(phaseStartTime);
    Serial.print(F("BPPV started - side: ")); Serial.println(side);
    Serial.print(F("Primary eye: ")); Serial.println(side);
}

// **********************************************
// BPPV state machine
// motorDir applied to torsion and horizontal only
// Vertical always upbeat regardless of side
//
// Phase 1: latency drift, Phase 2: crescendo, Phase 3: nystagmus beating
// (the scored diagnostic pattern), Phase 4: decrescendo, Phase 5: reversal
// (drives back the opposite way after the scored interval ends).
// **********************************************
void runMotorStep() {
    unsigned long now = millis();
    float phaseT = (now - phaseStartTime) / 1000.0;
    float dt = constrain((now - lastMotorStep) / 1000.0, 0.0, 0.05);
    lastMotorStep = now;

    switch (currentPhase) {

        case 1: {
            float target = LATENCY_DRIFT_DEG * (phaseT / (LATENCY_MS / 1000.0));
            eyeTorsion = -motorDir * target * 0.8;
            eyeVertical = target * 0.6;
            eyeHorizontal = 0.0;
            if (phaseT >= LATENCY_MS / 1000.0) advancePhase(now);
            break;
        }

        case 2: {
            float frac = constrain(phaseT / (CRESCENDO_MS / 1000.0), 0.0, 1.0);
            float targetTorsion = -motorDir * CRESCENDO_TARGET_TORSION * frac;
            float targetVertical = CRESCENDO_TARGET_VERTICAL * frac;
            float targetHoriz = -motorDir * PEAK_HORIZONTAL_DEG * frac * 0.4;
            float maxStep = 15.0 * dt;
            eyeTorsion += constrain(targetTorsion - eyeTorsion, -maxStep, maxStep);
            eyeVertical += constrain(targetVertical - eyeVertical, -maxStep, maxStep);
            eyeHorizontal += constrain(targetHoriz - eyeHorizontal, -maxStep, maxStep);
            clampEyeAngles();
            if (phaseT >= CRESCENDO_MS / 1000.0) advancePhase(now);
            break;
        }

        case 3: {
            float envelope = exp(-phaseT / TAU_DECAY);
            float driftTarget = BEAT_DRIFT_DEG * envelope;

            if (beatState == BEAT_DRIFT) {
                eyeTorsion += motorDir * BEAT_DRIFT_SPEED_TOR * dt;
                eyeVertical -= BEAT_DRIFT_SPEED_VER * dt;
                eyeHorizontal += motorDir * BEAT_DRIFT_SPEED_HOR * dt;

                if (abs(eyeTorsion) >= driftTarget || abs(eyeVertical) >= driftTarget * 0.9) {
                    beatState = BEAT_SNAP;
                    Serial.print(F("DRIFT_END:")); Serial.print(now);
                    Serial.print(F(",")); Serial.println(eyeTorsion, 3);
                }
            }
            else {
                float snapStep = BEAT_SNAP_SPEED * dt;
                if (eyeTorsion > 0.0) eyeTorsion = max(0.0f, eyeTorsion - snapStep);
                else if (eyeTorsion < 0.0) eyeTorsion = min(0.0f, eyeTorsion + snapStep);

                if (eyeVertical > 0.0) eyeVertical = max(0.0f, eyeVertical - snapStep);
                else if (eyeVertical < 0.0) eyeVertical = min(0.0f, eyeVertical + snapStep);

                eyeHorizontal *= 0.85;

                if (fabs(eyeTorsion) <= 0.01 && fabs(eyeVertical) <= 0.01) {
                    eyeHorizontal = 0.0;
                    beatState = BEAT_DRIFT;
                    Serial.print(F("DRIFT_START:")); Serial.print(now);
                    Serial.print(F(",")); Serial.println(eyeTorsion, 3);
                }
            }
            clampEyeAngles();
            if (phaseT >= NYSTAGMUS_MS / 1000.0) advancePhase(now);
            break;
        }

        case 4: {
            float fade = constrain(1.0 - phaseT / (DECRESCENDO_MS / 1000.0), 0.0, 1.0);
            float driftTarget = BEAT_DRIFT_DEG * fade * 0.5;

            if (beatState == BEAT_DRIFT) {
                eyeTorsion -= motorDir * BEAT_DRIFT_SPEED_TOR * fade * dt;
                eyeVertical += BEAT_DRIFT_SPEED_VER * fade * dt;
                eyeHorizontal -= motorDir * BEAT_DRIFT_SPEED_HOR * fade * dt;
                if (abs(eyeTorsion) >= driftTarget || fade < 0.05) beatState = BEAT_SNAP;
            }
            else {
                float snapStep = BEAT_SNAP_SPEED * fade * dt;
                if (abs(eyeTorsion) > 0.3) {
                    eyeTorsion += eyeTorsion > 0 ? -snapStep : snapStep;
                } else {
                    eyeTorsion = 0.0;
                }
                if (abs(eyeVertical) > 0.3) {
                    eyeVertical += eyeVertical > 0 ? -snapStep : snapStep;
                } else {
                    eyeVertical = 0.0;
                }
                eyeHorizontal *= 0.9;
                if (abs(eyeTorsion) <= 0.3 && abs(eyeVertical) <= 0.3) {
                    eyeTorsion = 0.0; eyeVertical = 0.0; beatState = BEAT_DRIFT;
                }
            }
            clampEyeAngles();
            if (phaseT >= DECRESCENDO_MS / 1000.0) advancePhase(now);
            break;
        }

        case 5: {
            float fade = exp(-phaseT / 3.0);
            eyeTorsion -= motorDir * 9.4 * fade * dt;
            eyeVertical += 11.3 * fade * dt;
            eyeHorizontal += motorDir * 6.3 * fade * dt;
            eyeHorizontal = constrain(eyeHorizontal, -6.0, 6.0);
            clampEyeAngles();
            if (phaseT >= REVERSAL_MS / 1000.0) allNeutral();
            break;
        }
    }

    writeAllMotors();
}

// **********************************************
// Broadcast to Unity, once per LOOP_INTERVAL_MS.
// TS: is the Arduino send-time used for the Unity-side latency
// measurement in the dissertation's latency evaluation.
// **********************************************
void broadcastState() {
    Serial.print(F("H:"));
    Serial.print(imuYaw, 2); Serial.print(F(","));
    Serial.print(imuPitch, 2); Serial.print(F(","));
    Serial.print(imuRoll, 2); Serial.print(F("|T:"));
    Serial.print(eyeTorsion, 2); Serial.print(F(",V:"));
    Serial.print(eyeVertical, 2); Serial.print(F(",H:"));
    Serial.print(eyeHorizontal, 2); Serial.print(F(",P:"));
    Serial.print(currentPhase); Serial.print(F(",S:"));
    Serial.print(motorDir == 1 ? F("R") : F("L")); Serial.print(F(",X:"));
    Serial.print(tiltFromVertical, 2); Serial.print(F(",TS:"));
    Serial.println(millis());
}

// --- IMU ---
EulerAngles quaternionToEuler() {
    // Extract angles directly from the quaternion - no gimbal lock.
    // Standard quaternion-to-Euler conversion, computed to avoid the
    // singularity at pitch = 90 deg.
    float w = q.w, x = q.x, y = q.y, z = q.z;

    // Pitch - rotation around Y axis (forward/back tilt); closed-form conversion
    float sinp = constrain(2.0 * (w * y - z * x), -1.0, 1.0);
    float pitch = asin(sinp) * 180.0 / M_PI;

    // Roll - rotation around X axis
    float sinr = 2.0 * (w * x + y * z);
    float cosr = 1.0 - 2.0 * (x * x + y * y);
    float roll = atan2(sinr, cosr) * 180.0 / M_PI;

    // Yaw - rotation around Z axis. Still has gimbal lock near pitch=90,
    // but the head never reaches 90 deg pitch during Dix-Hallpike, so it
    // remains stable in practice.
    float siny = 2.0 * (w * z + x * y);
    float cosy = 1.0 - 2.0 * (y * y + z * z);
    float yaw = atan2(siny, cosy) * 180.0 / M_PI;

    float tilt = acos(constrain(1.0 - 2.0 * (x * x + y * y), -1.0, 1.0)) * 180.0 / M_PI;

    return EulerAngles{pitch, roll, yaw, tilt};
}

int consecutiveIMUFailures = 0;
void readIMU() {
    if (!dmpReady) return;

    uint16_t fifoCount = mpu.getFIFOCount();

    if (fifoCount == 1024) {
        mpu.resetFIFO();
        consecutiveIMUFailures++;
        Serial.print(F("IMU: FIFO overflow, reset. fails=")); Serial.println(consecutiveIMUFailures);
        if (consecutiveIMUFailures >= 20) { restartDMP(); consecutiveIMUFailures = 0; }
        return;
    }

    if (!mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
        consecutiveIMUFailures++;
        if (consecutiveIMUFailures % 20 == 0) {
            Serial.print(F("IMU: no packet, fifoCount=")); Serial.print(fifoCount);
            Serial.print(F(" fails=")); Serial.println(consecutiveIMUFailures);
        }
        if (consecutiveIMUFailures >= 100) { restartDMP(); consecutiveIMUFailures = 0; }
        return;
    }

    consecutiveIMUFailures = 0;

    // Discard extra packets so FIFO never accumulates
    while (mpu.getFIFOCount() >= packetSize)
        mpu.getFIFOBytes(fifoBuffer, packetSize);

    mpu.dmpGetQuaternion(&q, fifoBuffer);

    EulerAngles angles = quaternionToEuler();

    // Apply calibration offsets
    headYaw = angles.yaw - yawOffset;
    headPitch = angles.pitch - pitchOffset;
    headRoll = angles.roll - rollOffset;

    // Wrap yaw to -180..180
    if (headYaw > 180) headYaw -= 360;
    if (headYaw < -180) headYaw += 360;

    // Apply Kalman filter - reduces noise on all three axes
    imuYaw = kalYaw.update(headYaw);
    imuPitch = kalPitch.update(headPitch);
    imuRoll = kalRoll.update(headRoll);
    tiltFromVertical = angles.tilt;
}

void calibrateSensor() {
    float yawSum = 0, pitchSum = 0, rollSum = 0;
    for (int i = 0; i < calibrationSamples; i++) {
        if (mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
            mpu.dmpGetQuaternion(&q, fifoBuffer);

            EulerAngles angles = quaternionToEuler();

            yawSum += angles.yaw;
            pitchSum += angles.pitch;
            rollSum += angles.roll;
        }
        delay(10);
    }
    yawOffset = yawSum / calibrationSamples;
    pitchOffset = pitchSum / calibrationSamples;
    rollOffset = rollSum / calibrationSamples;

    Serial.print(F("Calibration done Y:")); Serial.print(yawOffset, 2);
    Serial.print(F(" P:")); Serial.print(pitchOffset, 2);
    Serial.print(F(" R:")); Serial.println(rollOffset, 2);
    while (Serial.available()) Serial.read();
    serialBufferLen = 0;
    serialBuffer[0] = '\0';
}

void restartDMP() {
    mpu.setDMPEnabled(false);
    delay(50);
    mpu.resetFIFO();
    delay(50);
    mpu.setDMPEnabled(true);
    packetSize = mpu.dmpGetFIFOPacketSize();
    Serial.println(F("DMP restarted"));
}

// **********************************************
// Serial command dispatcher.
// Built around the fixed char serialBuffer[] (not String) - no heap
// allocation, no fragmentation risk. Comparisons use strcmp/strncmp.
//
// Core exam commands: R/TRIGGER, L, N/NEUTRAL, RESET.
// The remaining two branches (J, C:) dispatch into SECTION 2 below and
// only exist to collect evaluation data; they never run during a normal
// triggered exam.
// **********************************************
void checkSerialCommands() {
    while (Serial.available()) {
        char c = Serial.read();
        if (c == '\n' || c == '\r') {
            if (serialBufferLen > 0) {
                serialBuffer[serialBufferLen] = '\0';
                Serial.print(F("CMD:")); Serial.println(serialBuffer);

                if ((strcmp(serialBuffer, "TRIGGER") == 0 || strcmp(serialBuffer, "R") == 0) && !motorRunning && !testModeActive)
                    startBPPV('R');
                else if (strcmp(serialBuffer, "L") == 0 && !motorRunning && !testModeActive)
                    startBPPV('L');
                else if (strcmp(serialBuffer, "NEUTRAL") == 0 || strcmp(serialBuffer, "N") == 0) {
                    allNeutral();
                    Serial.println(F("Motors neutral"));
                }
                else if (strcmp(serialBuffer, "RESET") == 0) {
                    mpu.resetFIFO();
                    allNeutral();
                    Serial.println(F("RESET_OK"));
                }
                // --- data collection (SECTION 2) ---
                else if (strcmp(serialBuffer, "J") == 0) {
                    startJitterTrial();
                }
                else if (strncmp(serialBuffer, "C:", 2) == 0) {
                    handleTestModeCommand(serialBuffer + 2);
                }
            }
            serialBufferLen = 0;
            serialBuffer[0] = '\0';
        }
        else if (c >= 32 && c < 127) {
            if (serialBufferLen < SERIAL_BUF_SIZE - 1) {
                serialBuffer[serialBufferLen++] = c;
                serialBuffer[serialBufferLen] = '\0';
            } else {
                // overflow guard, same intent as the old ">20 chars" reset
                serialBufferLen = 0;
            }
        }
    }
}


// ================================================================
// SECTION 2 - DATA COLLECTION FOR EVALUATION
// Nothing here runs during a normal triggered exam. Both pieces exist
// solely to produce the data behind the dissertation's evaluation
// section, and are reached only via the J / C: serial commands above.
// ================================================================

// --- Positional-sync calibration / validation test mode ---
// Drives the eye to a fixed commanded position outside the exam state
// machine, so it can be photographed and measured to build/validate the
// calibration tables in SECTION 1 (see Positional Synchronisation,
// dissertation System Integration evaluation).
//
// Command form: C:<torsion>,<vertical>,<horizontal>  or  C:EXIT
// Takes a plain char* (not String) and parses it with strtok/atof.
// strtok writes '\0' into the buffer at each comma, so this consumes and
// modifies `payload` - fine here, since it's only ever called once per
// received line, right before checkSerialCommands() clears the buffer.
void handleTestModeCommand(char* payload) {
    if (strcmp(payload, "EXIT") == 0) {
        allNeutral(); // resets angles, currentPhase=0, and clears testModeActive
        Serial.println(F("Test hold mode exited"));
        return;
    }

    char* tPart = strtok(payload, ",");
    char* vPart = strtok(NULL, ",");
    char* hPart = strtok(NULL, ",");

    if (tPart == NULL || vPart == NULL || hPart == NULL) {
        Serial.println(F("Bad C: command. Use  C:<torsion>,<vertical>,<horizontal>  e.g. C:20,0,0"));
        return;
    }

    eyeTorsion    = atof(tPart);
    eyeVertical   = atof(vPart);
    eyeHorizontal = atof(hPart);
    clampEyeAngles();

    motorRunning = false;   // don't let the exam state machine run at the same time
    testModeActive = true;
    currentPhase = -1;      // distinct sentinel so Unity/InstructionManager can ignore it

    Serial.print(F("TEST_HOLD T:")); Serial.print(eyeTorsion, 2);
    Serial.print(F(" V:")); Serial.print(eyeVertical, 2);
    Serial.print(F(" H:")); Serial.println(eyeHorizontal, 2);
}

// --- IMU jitter / noise trial ---
// Characterises head-tracking noise (raw vs. Kalman-filtered) for the
// dissertation's IMU/head evaluation: hold the head still, send "J",
// and after JITTER_SAMPLES readings (~3s) the mean/SD of each channel is
// printed for use in the evaluation's graphs and tables.
void printStat(const char* label, RunningStats &s) {
    Serial.print(label);
    Serial.print(F(": n=")); Serial.print(s.n);
    Serial.print(F(" mean=")); Serial.print(s.mean, 3);
    Serial.print(F(" sd=")); Serial.println(s.stddev(), 3);
}

void startJitterTrial() {
    if (SKIP_IMU_TEST) {
        Serial.println(F("Jitter trial needs real IMU data, SKIP_IMU_TEST is true."));
        return;
    }
    rawYawStats.reset();   filtYawStats.reset();
    rawPitchStats.reset(); filtPitchStats.reset();
    rawRollStats.reset();  filtRollStats.reset();
    tiltStats.reset();
    jitterTrialActive = true;
    Serial.println(F("Jitter trial started. Hold pose steady..."));
}

void jitterTrialSample() {
    rawYawStats.update(headYaw);     filtYawStats.update(imuYaw);
    rawPitchStats.update(headPitch); filtPitchStats.update(imuPitch);
    rawRollStats.update(headRoll);   filtRollStats.update(imuRoll);
    tiltStats.update(tiltFromVertical);

    if (rawYawStats.n >= JITTER_SAMPLES) {
        jitterTrialActive = false;
        Serial.println(F("=== JITTER TRIAL SUMMARY ==="));
        printStat("yaw_raw", rawYawStats);     printStat("yaw_filt", filtYawStats);
        printStat("pitch_raw", rawPitchStats); printStat("pitch_filt", filtPitchStats);
        printStat("roll_raw", rawRollStats);   printStat("roll_filt", filtRollStats);
        printStat("tilt_raw", tiltStats);
        Serial.println(F("Send 'J' to run another trial at a new pose."));
    }
}


// **********************************************
// SETUP
// **********************************************
void setup() {
    Wire.begin();
    Wire.setClock(400000);
    Serial.begin(115200);

    Serial.println(F("Initialising PCA9685..."));
    pca.begin();
    pca.setPWMFreq(PWM_FREQ);
    delay(100);
    allNeutral();

    if (SKIP_IMU_TEST) {
        Serial.println(F("TEST MODE: Skipping IMU. Auto-triggering in 2s..."));
        Serial.print(F("Side: ")); Serial.println(BPPV_SIDE);
        delay(2000);
        startBPPV(BPPV_SIDE);
    }
    else {
        Serial.println(F("MPU6050 DMP initializing..."));
        mpu.initialize();
        devStatus = mpu.dmpInitialize();

        if (devStatus == 0) {
            mpu.CalibrateAccel(6);
            mpu.CalibrateGyro(6);
            mpu.setDMPEnabled(true);
            dmpReady = true;
            packetSize = mpu.dmpGetFIFOPacketSize();
            Serial.print(F("Waiting 15s for sensor to settle..."));
            delay(stabilizationDelay);
            Serial.println(F(" Done!"));
            calibrateSensor();
        }
        else {
            Serial.print(F("DMP init failed (code "));
            Serial.print(devStatus); Serial.println(F(")"));
        }

        Serial.println(F("Ready. R/TRIGGER=right BPPV;  L=left BPPV;  N/NEUTRAL=reset;  C:t,v,h=hold test angle;  C:EXIT=leave test mode"));
    }
}

// **********************************************
// LOOP
// **********************************************
void loop() {
    checkSerialCommands();

    // Drain FIFO silently when no serial activity.
    // Prevents 20-30 min overflow without touching DMP state.
    if (!SKIP_IMU_TEST) {
        uint16_t count = mpu.getFIFOCount();
        if (count >= 1024) {
            mpu.resetFIFO();
            return;
        }
        readIMU();
        if (jitterTrialActive) jitterTrialSample();
    }

    // While holding a fixed test angle (SECTION 2), skip the exam state
    // machine entirely and just keep driving the motors at the held values.
    if (testModeActive) {
        writeAllMotors();
    } else if (motorRunning) {
        runMotorStep();
    }

    static unsigned long lastBroadcast = 0;
    unsigned long now = millis();
    if (now - lastBroadcast >= LOOP_INTERVAL_MS) {
        broadcastState();
        lastBroadcast = now;
    }
}