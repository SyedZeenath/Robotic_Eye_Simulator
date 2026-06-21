/*
 * BPPV Robotic Eye Simulation
 * MPU6050 Head Tracker + PCA9685 Motor Driver
 *
 * Right eye: CH0=horizontal, CH1=vertical, CH2=torsion
 * Left eye:  CH4=horizontal, CH5=vertical, CH6=torsion
 *
 * R or TRIGGER → right posterior BPPV (right eye only, left eye neutral)
 * L            → left posterior BPPV  (left eye only, right eye neutral)
 * Both eyes move with mirror effect — horizontal and torsion signs flip
 * N or NEUTRAL → all motors neutral
 */

#include <Wire.h>
#include <I2Cdev.h>
#include <MPU6050_6Axis_MotionApps20.h>
#include <Adafruit_PWMServoDriver.h>

// **********************************************
// TEST FLAG
// **********************************************
#define SKIP_IMU_TEST   false
#define BPPV_SIDE       'R'

// **********************************************
// IMU
// **********************************************
MPU6050 mpu;
bool dmpReady = false;
uint8_t mpuIntStatus, devStatus;
uint16_t packetSize, fifoCount;
uint8_t fifoBuffer[64];
Quaternion q;
VectorFloat gravity;
float ypr[3];
float yawOffset = 0, pitchOffset = 0, rollOffset = 0;
const unsigned long stabilizationDelay = 15000;
const int calibrationSamples = 100;
float imuYaw = 0, imuPitch = 0, imuRoll = 0;

// **********************************************
// PCA9685
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

#define GAIN_TORSION    4.0
#define GAIN_VERTICAL   3.5
#define GAIN_HORIZONTAL 3.5

// **********************************************
// BPPV clinical parameters
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
// Motor state
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
unsigned long lastUnityPing  = 0;
bool unityConnected = false;

String serialBuffer = "";

struct Kalman1D {
    float process_noise = 0.001;  // process noise
    float measured_noise = 0.05;   // measurement noise 
    float error_estimate = 1.0;    // estimate error covariance
    float current_estimate = 0.0;    // current estimate
    
    float update(float measured_value) {
        error_estimate += process_noise;  // Prediction step - noise grows by process noise each step
        float kalman_gain = error_estimate / (error_estimate + measured_noise); // Kalman gain - how much to trust measurement vs prediction
        current_estimate += kalman_gain * (measured_value - current_estimate); // Update estimate
        error_estimate *= (1.0 - kalman_gain); // Update error covariance
        return current_estimate;
    }
};

Kalman1D kalYaw, kalPitch, kalRoll;

// **********************************************
// Broadcast
// **********************************************
void broadcastState() {
    Serial.print("H:");
    Serial.print(imuYaw, 2); Serial.print(",");
    Serial.print(imuPitch, 2); Serial.print(",");
    Serial.print(imuRoll, 2);
    Serial.print("|T:");
    Serial.print(eyeTorsion, 2); Serial.print(",V:");
    Serial.print(eyeVertical, 2); Serial.print(",H:");
    Serial.print(eyeHorizontal, 2); Serial.print(",P:");
    Serial.print(currentPhase); Serial.print(",S:");
    Serial.println(motorDir == 1 ? "R" : "L");
}

// **********************************************
// Motor utilities
// **********************************************
uint16_t angleToPulse(float angle) {
    angle = constrain(angle, SERVO_MIN, SERVO_MAX);
    return (uint16_t)(PULSE_MIN + (angle / 180.0) * (PULSE_MAX - PULSE_MIN));
}

float eyeToServo(float eyeAngle, float gain) {
    return constrain(SERVO_NEUTRAL + (eyeAngle * gain), (float)SERVO_MIN, (float)SERVO_MAX);
}

// **********************************************
// writeAllMotors
//
//   Vertical:   same sign (both eyes go up)
//   Horizontal: opposite sign (both eyes move toward nose = convergent)
//   Torsion:    opposite sign (both tops tilt same direction in space)
//
// Right BPPV: right eye = primary
// Left BPPV:  left eye = primary
// **********************************************
void writeAllMotors() {
    if (activeEye == 'R') {
        pca.setPWM(R_CH_TORSION, 0, angleToPulse(eyeToServo(-eyeTorsion, GAIN_TORSION)));
        pca.setPWM(R_CH_VERTICAL, 0, angleToPulse(eyeToServo(eyeVertical, GAIN_VERTICAL)));
        pca.setPWM(R_CH_HORIZONTAL, 0, angleToPulse(eyeToServo(eyeHorizontal, GAIN_HORIZONTAL)));

        pca.setPWM(L_CH_TORSION, 0, PULSE_NEUTRAL);
        pca.setPWM(L_CH_VERTICAL, 0, PULSE_NEUTRAL);
        pca.setPWM(L_CH_HORIZONTAL, 0, PULSE_NEUTRAL);
    }
    else {
        pca.setPWM(L_CH_TORSION, 0, angleToPulse(eyeToServo( eyeTorsion, GAIN_TORSION)));
        pca.setPWM(L_CH_VERTICAL, 0, angleToPulse(eyeToServo( eyeVertical, GAIN_VERTICAL)));
        pca.setPWM(L_CH_HORIZONTAL, 0, angleToPulse(eyeToServo( eyeHorizontal, GAIN_HORIZONTAL)));

        pca.setPWM(R_CH_TORSION, 0, PULSE_NEUTRAL);
        pca.setPWM(R_CH_VERTICAL, 0, PULSE_NEUTRAL);
        pca.setPWM(R_CH_HORIZONTAL, 0, PULSE_NEUTRAL);
    }
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
}

void clampEyeAngles() {
    eyeTorsion = constrain(eyeTorsion, -PEAK_TORSION_DEG, PEAK_TORSION_DEG);
    eyeVertical = constrain(eyeVertical, -PEAK_VERTICAL_DEG, PEAK_VERTICAL_DEG);
    eyeHorizontal = constrain(eyeHorizontal, -PEAK_HORIZONTAL_DEG, PEAK_HORIZONTAL_DEG);
}

void advancePhase(unsigned long now) {
    currentPhase++;
    phaseStartTime = now;
    lastMotorStep = now;
    beatState = BEAT_DRIFT;
    Serial.print("PHASE:"); Serial.println(currentPhase);
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
    Serial.print("BPPV started - side: "); Serial.println(side);
    Serial.print("Primary eye: "); Serial.println(side);
}

// **********************************************
// BPPV state machine - unchanged from before
// motorDir applied to torsion and horizontal only
// Vertical always upbeat regardless of side
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
                // Slow phase - drifts DOWNWARD + top tilts AWAY from affected ear
                // This is the quiet drift between beats
                eyeTorsion += motorDir * BEAT_DRIFT_SPEED_TOR * dt; // flipped
                eyeVertical -= BEAT_DRIFT_SPEED_VER * dt; // downward
                eyeHorizontal += motorDir * BEAT_DRIFT_SPEED_HOR * dt; // flipped

                if (abs(eyeTorsion) >= driftTarget || abs(eyeVertical) >= driftTarget * 0.9)
                    beatState = BEAT_SNAP;
            }
            else {
                // Fast phase - snaps UPWARD + top tilts TOWARD affected ear
                // This is the visible flick
                float snapStep = BEAT_SNAP_SPEED * dt;
                if (abs(eyeTorsion) > 0.5) eyeTorsion += eyeTorsion > 0 ? -snapStep : snapStep;
                else eyeTorsion = 0.0;
                if (abs(eyeVertical) > 0.5) eyeVertical += eyeVertical > 0 ? -snapStep : snapStep;
                else eyeVertical = 0.0;
                eyeHorizontal *= 0.85;

                if (abs(eyeTorsion) <= 0.5 && abs(eyeVertical) <= 0.5) {
                    eyeTorsion = 0.0; eyeVertical = 0.0; eyeHorizontal = 0.0;
                    beatState = BEAT_DRIFT;
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
                if (abs(eyeTorsion) > 0.3) eyeTorsion += eyeTorsion > 0 ? -snapStep : snapStep; else eyeTorsion = 0.0;
                if (abs(eyeVertical) > 0.3) eyeVertical += eyeVertical > 0 ? -snapStep : snapStep; else eyeVertical = 0.0;
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
            eyeTorsion -= motorDir * 9.4 * fade * dt; // flipped
            eyeVertical += 11.3 * fade * dt; // upward reversal
            eyeHorizontal += motorDir * 6.3 * fade * dt; // flipped
            eyeHorizontal = constrain(eyeHorizontal, -6.0, 6.0);
            clampEyeAngles();
            if (phaseT >= REVERSAL_MS / 1000.0) allNeutral();
            break;
        }
    }

    writeAllMotors();
}

// **********************************************
// Serial commands
// **********************************************
void checkSerialCommands() {
    while (Serial.available()) {
        char c = Serial.read();
        if (c == '\n' || c == '\r') {
            serialBuffer.trim();
            if (serialBuffer.length() > 0) {
                lastUnityPing = millis();
                unityConnected = true; 
                Serial.print("CMD:"); Serial.println(serialBuffer);
                if ((serialBuffer == "TRIGGER" || serialBuffer == "R") && !motorRunning)
                    startBPPV('R');
                else if (serialBuffer == "L" && !motorRunning)
                    startBPPV('L');
                else if (serialBuffer == "NEUTRAL" || serialBuffer == "N") {
                    allNeutral();
                    Serial.println("Motors neutral");
                }
            }
            serialBuffer = "";
        }
        else if (c >= 32 && c < 127) {
            serialBuffer += c;
            if (serialBuffer.length() > 20) serialBuffer = "";
        }
    }
}

// **********************************************
// IMU
// **********************************************
void readIMU() {
    if (!dmpReady) return;
    if (!mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) return;

    mpu.dmpGetQuaternion(&q, fifoBuffer);

    // Extract angles directly from quaternion - no gimbal lock
    // These formulas are the standard quaternion to Euler conversion
    // but computed in a way that avoids the singularity at pitch=90
    float w = q.w, x = q.x, y = q.y, z = q.z;

    // Pitch - rotation around X axis (forward/back tilt)
    // Uses atan2 which handles all quadrants correctly
    float sinp = 2.0 * (w * y - z * x);
    sinp = constrain(sinp, -1.0, 1.0); // clamp to avoid asin domain error
    headPitch = asin(sinp) * 180.0 / M_PI;

    // Roll - rotation around Y axis
    float sinr = 2.0 * (w * x + y * z);
    float cosr = 1.0 - 2.0 * (x * x + y * y);
    headRoll = atan2(sinr, cosr) * 180.0 / M_PI;

    // Yaw - rotation around Z axis
    // This still has gimbal lock near pitch=90 but for Dix-Hallpike
    // the head never reaches 90 degrees pitch so it remains stable
    float siny = 2.0 * (w * z + x * y);
    float cosy = 1.0 - 2.0 * (y * y + z * z);
    float rawYaw = atan2(siny, cosy) * 180.0 / M_PI;

    // Apply calibration offsets
    headYaw = rawYaw - yawOffset;
    headPitch = headPitch - pitchOffset;
    headRoll = headRoll - rollOffset;

    // Wrap yaw to -180 to 180
    if (headYaw > 180) headYaw -= 360;
    if (headYaw < -180) headYaw += 360;

    // Apply Kalman filter - reduces noise on all three axes
    imuYaw = kalYaw.update(headYaw);
    imuPitch = kalPitch.update(headPitch);
    imuRoll = kalRoll.update(headRoll);
}

void calibrateSensor() {
    float yawSum = 0, pitchSum = 0, rollSum = 0;
    for (int i = 0; i < calibrationSamples; i++) {
        if (mpu.dmpGetCurrentFIFOPacket(fifoBuffer)) {
            mpu.dmpGetQuaternion(&q, fifoBuffer);
            float w = q.w, x = q.x, y = q.y, z = q.z;

            float sinp = constrain(2.0*(w*y - z*x), -1.0, 1.0);
            float pitch = asin(sinp) * 180.0 / M_PI;

            float sinr = 2.0*(w*x + y*z);
            float cosr = 1.0 - 2.0*(x*x + y*y);
            float roll = atan2(sinr, cosr) * 180.0 / M_PI;

            float siny = 2.0*(w*z + x*y);
            float cosy = 1.0 - 2.0*(y*y + z*z);
            float yaw = atan2(siny, cosy) * 180.0 / M_PI;

            yawSum += yaw;
            pitchSum += pitch;
            rollSum += roll;
        }
        delay(10);
    }
    yawOffset = yawSum / calibrationSamples;
    pitchOffset = pitchSum / calibrationSamples;
    rollOffset = rollSum / calibrationSamples;

    Serial.print("Calibration done Y:"); Serial.print(yawOffset, 2);
    Serial.print(" P:"); Serial.print(pitchOffset, 2);
    Serial.print(" R:"); Serial.println(rollOffset, 2);
    while (Serial.available()) Serial.read();
    serialBuffer = "";
}

// **********************************************
// SETUP
// **********************************************
void setup() {
    Wire.begin();
    Wire.setClock(400000);
    Serial.begin(115200);

    Serial.println("Initialising PCA9685...");
    pca.begin();
    pca.setPWMFreq(PWM_FREQ);
    delay(100);
    allNeutral();

    if (SKIP_IMU_TEST) {
        Serial.println("TEST MODE: Skipping IMU. Auto-triggering in 2s...");
        Serial.print("Side: "); Serial.println(BPPV_SIDE);
        delay(2000);
        startBPPV(BPPV_SIDE);
    }
    else {
        Serial.println("MPU6050 DMP initializing...");
        mpu.initialize();
        devStatus = mpu.dmpInitialize();

        if (devStatus == 0) {
            mpu.CalibrateAccel(6);
            mpu.CalibrateGyro(6);
            mpu.setDMPEnabled(true);
            dmpReady = true;
            packetSize = mpu.dmpGetFIFOPacketSize();
            Serial.print("Waiting 15s for sensor to settle...");
            delay(stabilizationDelay);
            Serial.println(" Done!");
            calibrateSensor();
        }
        else {
            Serial.print("DMP init failed (code ");
            Serial.print(devStatus); Serial.println(")");
        }

        Serial.println("Ready. R/TRIGGER=right BPPV;  L=left BPPV;  N/NEUTRAL=reset");
    }
}

// **********************************************
// LOOP
// **********************************************
void loop() {
    // Watchdog - if no message from Unity for 5 seconds, flush buffer
    if (millis() - lastUnityPing > 5000 && unityConnected) {
        while (Serial.available()) Serial.read();
        serialBuffer = "";
        unityConnected = false;
        Serial.println("Unity disconnected — buffer flushed");
    }

    checkSerialCommands();
    if (!SKIP_IMU_TEST) readIMU();
    if (motorRunning) runMotorStep();

    static unsigned long lastBroadcast = 0;
    unsigned long now = millis();
    if (now - lastBroadcast >= LOOP_INTERVAL_MS) {
        broadcastState();
        lastBroadcast = now;
    }
}