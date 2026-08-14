# Robotic Eye Movement Simulator for the Dix-Hallpike Test

## Overview

This dissertation project implements a robotic eye movement system for simulating the eye responses associated with the Dix-Hallpike test for BPPV research and training. The system combines Arduino-based hardware control, IMU head tracking, servo-driven eye movement and a Unity-based digital interface.

The platform provides real-time eye movement simulation, a guided clinical test workflow, patient data management, calibration tools and performance evaluation. The physical robotic head and the Unity model communicate through a USB serial connection.

## System Architecture

The system is divided into four main parts:

1. **Physical robotic platform** - A head and eye mechanism driven by six servo motors.
2. **Arduino controller** - Reads head movement from the MPU6050, controls the eye motors through the PCA9685 and communicates with Unity.
3. **Unity frontend** - Displays the digital head and eye model, manages the test workflow and provides monitoring and patient-management functions.
4. **Evaluation tools and data** - Calibration tools and recorded CSV data are used to evaluate positional synchronisation, latency and IMU behaviour.

### Data Flow

```text
Physical head movement / IMU
            ↓
        Arduino
            ↓
     USB Serial 115200
            ↓
 ArduinoSerialReader
            ↓
 RealisticEyeController
            ↓
  Unity head and eye model
            ↓
        User display
```

The Unity interface can also send commands back to the Arduino to start right-side, left-side or neutral sequences.

## Project Structure

### `/eye_controller/`

Arduino firmware for motor control, IMU tracking, BPPV sequence control, calibration and evaluation.

- **`eye_controller.ino`** - Main Arduino sketch containing:
  - MPU6050 head tracking using the Digital Motion Processor (DMP)
  - PCA9685 servo control
  - Six-channel eye movement control
  - Mirror-effect handling between the two eyes
  - Clinical test state machine
  - Physical-to-digital calibration lookup tables
  - Serial communication with Unity
  - Evaluation routines for positional synchronisation and IMU jitter/noise
  - `SKIP_IMU_TEST` option for bench testing without the IMU

### `/unity_frontend/`

Unity project containing the digital twin, user interface, clinical workflow and evaluation tools.

#### `Assets/Scripts/`

- **`ArduinoSerialReader.cs`** - Reads and parses serial data from the Arduino.
- **`RealisticEyeController.cs`** - Applies eye angles to the digital model and handles VOR compensation and head velocity tracking.
- **`SimulationUI.cs`** - Controls the main simulation interface.
- **`InstructionManager.cs`** - Controls the eight-step Dix-Hallpike instruction sequence.
- **`InstructionStep.cs`** - Defines an individual instruction step.
- **`DiagnosisFeedbackController.cs`** - Handles assessment feedback shown during the test.
- **`LatencyMonitor.cs`** - Records communication and timing information.
- **`SyncDebugOverlay.cs`** - Displays synchronisation and debugging information.
- **`EnvLoader.cs`** - Loads runtime environment and configuration settings.
- **`RecordingIndicator.cs`** - Displays recording status.
- **`TTS_Script.cs`** / **`STT_Script.cs`** - Text-to-speech and speech-to-text functions.
- **`MicrophoneRecorder.cs`** - Handles audio recording.
- **`WavUtility.cs`** - Provides WAV file handling utilities.

#### `Assets/Scripts/PatientModel/`

- **`PatientManager.cs`** - Maintains the current patient data.
- **`Patient.cs`** - Defines the patient data model.
- **`PatientList.cs`** - Manages the patient collection.
- **`PatientItemUI.cs`** - Displays an individual patient entry.
- **`PatientListUI.cs`** - Controls the patient list interface.

#### Other Unity folders

- **`Assets/Scenes/`**
  - `MainScene.unity` - Main simulation scene.
  - `SampleScene.unity` - Development and testing scene.
- **`Assets/Prefab/`** - Reusable head and eye components.
- **`Assets/Resources/`** - Patient JSON data, textures, materials and other runtime resources.
  - `patients.json` - Patient data used by the application.
- **`Assets/StreamingAssets/`** - Additional runtime data and configuration.
- **`Assets/Models/`** - 3D head and eye geometry.
- **`Assets/Materials/`** - Materials used by the scene.
- **`Packages/`** - Unity package configuration and dependencies.
- **`ProjectSettings/`** - Unity project and build settings.

Generated folders such as `Logs/` and `Temp/` are not required for the source project and can be omitted from version control.

### `/3D_models/`

CAD models for the physical robotic platform:

- **`Body_Of_Robotic_Bppv_Head.stl`** - Main head structure.
- **`Rod_Holder_Torso.stl`** - Torso mounting component.
- **`Eye_Mechanism/`** - Eye mechanism components.
- **`Head/`** - Head assembly components.
- **`Neck/`** - Neck joint components.
- **`Base_Hinge_Joint/`** - Base hinge components.

### `/helpers/`

- **`photo_angle_measurer.html`** - Browser-based tool used to measure physical eye angles during calibration.

### `/images/`

Evaluation and calibration material:

- **`Evaluations/Eye_angles/`** - Recorded eye-angle measurements.
- **`Evaluations/Head_angles/`** - Recorded head-angle measurements.
- Calibration screenshots and supporting images.

### `/unity_frontend/buildFiles/`

Evaluation data generated during testing:

- **`latency_results.csv`** - Serial communication latency measurements.
- **`sync_results.csv`** - Motor synchronisation measurements.
- **`transfer_latency_results.csv`** - Data transfer latency measurements.
- **`BPPV_Head_Twin_Data/`** - Evaluation data associated with the physical and digital head system.

## Hardware Requirements

The tested system uses:

- **Arduino UNO R3** - Main microcontroller.
- **MPU6050** - Six-axis accelerometer/gyroscope for head tracking.
- **PCA9685** - 16-channel PWM driver for servo control.
- **Six servo motors**:
  - Right eye: horizontal, vertical and torsional movement.
  - Left eye: horizontal, vertical and torsional movement.
- **Motor power supply** - Separate suitable supply for the servo motors.
- **USB connection** - Arduino-to-PC serial communication.

### PCA9685 Channel Mapping


| Eye   | Horizontal | Vertical | Torsion |
| ----- | ---------: | -------: | ------: |
| Right |        CH0 |      CH1 |     CH2 |
| Left  |        CH4 |      CH5 |     CH6 |

Channels CH3 and CH7-15 are unused by the current implementation.

## Software Requirements

- **Arduino IDE** 1.8.x or later.
- **I2Cdev** library.
- **MPU6050** library with DMP support.
- **Adafruit PWM Servo Driver** library.
- **Unity** 2022.x or later; an LTS release is recommended.
- **TextMesh Pro** for the Unity interface.
- **Windows audio system** for the TTS/STT functions.
- **Visual Studio** for C# development.

## Setup

### 1. Arduino

1. Install the Arduino IDE.
2. Install the required I2Cdev, MPU6050 and Adafruit PWM Servo Driver libraries.
3. Connect the MPU6050 and PCA9685 to the Arduino I2C interface.
4. Connect the six servo motors to the PCA9685 channels listed above.
5. Open `eye_controller/eye_controller.ino`.
6. Select the correct board and COM port.
7. Upload the firmware.

For bench testing without the IMU, set:

```cpp
SKIP_IMU_TEST = true
```

The serial interface uses **115200 baud**.

### 2. Unity

1. Install Unity 2022.x or a compatible LTS release.
2. Open the `unity_frontend/` directory through Unity Hub.
3. Allow Unity to complete the initial asset import.
4. Open `Assets/Scenes/MainScene.unity`.
5. Check the `ArduinoSerialReader` component and confirm the COM port.
6. Confirm the baud rate is set to 115200.
7. Check the patient data file if patient information needs to be changed.
8. Press **Play** to test the application in the Unity Editor.

TTS/STT functions require the appropriate Windows audio and microphone permissions.

## Calibration

The physical eye mechanism requires calibration before comparing physical and digital eye positions.

### Photo Angle Measurement

Open:

```text
helpers/photo_angle_measurer.html
```

The tool is used to measure physical eye angles and relate them to the corresponding digital motor commands. The resulting values are used by the calibration lookup tables in the Arduino firmware.

### Evaluation Data Collection

The firmware contains a separate evaluation section for:

- Positional synchronisation testing.
- IMU jitter and noise measurements.
- Calibration data collection.

The Unity application also provides `LatencyMonitor` and `SyncDebugOverlay` components for monitoring system behaviour during testing.

Evaluation results are stored in the `unity_frontend/buildFiles/` directory.

## Communication Protocol

Communication between Arduino and Unity uses text-based USB serial communication at 115200 baud.

### Unity → Arduino

```text
R                  Right-side BPPV sequence
L                  Left-side BPPV sequence
N                  Neutral position
C:<h>,<v>,<t>      Calibration command
```

### Arduino → Unity

The Arduino sends status and eye-angle information in a text format containing the current phase and horizontal, vertical and torsional angles.

The exact message format is implemented in `eye_controller.ino` and parsed by `ArduinoSerialReader.cs`.

## Running the System

### Bench Test

For motor-only testing:

1. Set `SKIP_IMU_TEST = true`.
2. Upload the Arduino firmware.
3. Open the Serial Monitor at 115200 baud.
4. Send `R`, `L` or `N`.
5. Check that the motors respond correctly.

### Full System Test

1. Connect the Arduino and physical eye mechanism.
2. Connect the MPU6050.
3. Start the Unity application.
4. Confirm that the serial connection is active.
5. Select a patient if required.
6. Start the Dix-Hallpike test workflow.
7. Follow the on-screen instructions.
8. Monitor the physical and digital eye movements.
9. Use the debugging and latency tools when collecting evaluation data.

### Clinical Test Workflow

The Unity interface provides an eight-step test sequence covering the right and left sides. The instruction system guides the user through the sequence, while the digital head and eye model display the corresponding movement.

Patient information and test outcomes can be stored through the patient-management system.

## Evaluation and Monitoring

The project includes tools for evaluating the physical and digital system.

### Latency

`LatencyMonitor.cs` records communication timing during system operation. Recorded latency results are stored as CSV data for later analysis.

### Positional Synchronisation

The physical eye positions are compared with their corresponding digital positions using calibration and synchronisation data.

### IMU Stability

The Arduino evaluation section can be used to collect IMU jitter and noise data while the head is held in controlled positions.

### Debugging

`SyncDebugOverlay.cs` can display:

- Current test phase.
- Motor angles.
- Head rotation.
- IMU information.
- Timing information.

These tools are intended for development and evaluation and can be disabled during normal operation.

## Key Features

### Robotic Eye and Head System

- Independent three-axis movement for each eye.
- Six servo-driven eye axes.
- MPU6050-based head tracking.
- PCA9685 PWM motor control.
- Calibration lookup tables for physical-to-digital angle mapping.
- Mirror-effect handling between the two eyes.

### Unity Digital Twin

- Real-time 3D head and eye visualisation.
- Serial-driven eye movement.
- Head rotation visualisation.
- VOR compensation.
- Guided Dix-Hallpike workflow.

### Patient and Test Management

- JSON-based patient data.
- Patient selection and management.
- Test result and diagnosis feedback.
- Eight-step test instruction system.
- TTS/STT functions.
- Microphone recording support.

### Evaluation

- Serial latency monitoring.
- Positional synchronisation testing.
- IMU jitter/noise testing.
- Calibration tools.
- CSV export of evaluation data.

## Technical Details

### Arduino Firmware

The firmware is organised into the following sections:

1. **Declarations** - Configuration values, calibration data, state variables and IMU data.
2. **BPPV Core** - Motor control, calibration, state-machine operation, head tracking and serial commands.
3. **Evaluation** - Positional synchronisation and IMU data collection.
4. **Main Loop** - Continuous IMU processing, motor updates, serial handling and telemetry.

The BPPV sequence is implemented as a state machine covering the required test phases.

### Eye Motor Control

The PCA9685 controls six servo channels. Physical motor positions are mapped to target angles using calibration data. Smoothing is applied to reduce visible movement caused by variations in serial updates.

The left and right eye movements use mirrored horizontal and torsional values where required by the mechanical arrangement.

### IMU and Head Tracking

The MPU6050 provides accelerometer and gyroscope measurements. Its Digital Motion Processor is used for quaternion-based motion processing. Head pitch, roll and yaw are used by the Unity system for head movement and VOR-related calculations.

### Data Persistence

- **Patient data:** JSON format in `Assets/Resources/patients.json`.
- **Evaluation results:** CSV files in `unity_frontend/`.
- **Calibration:** Stored in the Arduino firmware calibration data.

## Building the Unity Application

1. Open **File → Build Settings**.
2. Add `Assets/Scenes/MainScene.unity`.
3. Select Windows as the target platform used for this project.
4. Configure the required resolution and presentation settings.
5. Build the standalone application.
6. On the target system, connect the Arduino and confirm the serial port configuration.

The application was developed and tested on Windows 10/11.

## Troubleshooting

### Serial connection is not detected

- Check that the Arduino is connected.
- Confirm the COM port in `ArduinoSerialReader`.
- Confirm that both sides use 115200 baud.
- Close other applications using the same serial port.
- Restart the Arduino and Unity application if necessary.

### Motors do not move

- Check the PCA9685 I2C connection.
- Check the PCA9685 address.
- Check the separate motor power supply.
- Check the servo connections.
- Test the `R`, `L` and `N` commands through the Serial Monitor.

### IMU does not initialise

- Check the MPU6050 SDA/SCL connections.
- Confirm that the required MPU6050 and I2Cdev libraries are installed.
- Check the I2C address.
- Use `SKIP_IMU_TEST = true` for motor-only testing.

### Eye movement is unstable

- Check the motor power supply.
- Check USB and I2C connections.
- Check the smoothing setting in `RealisticEyeController`.
- Use the synchronisation overlay to identify whether the problem originates from the physical or digital side.

### TTS/STT does not work

- Check Windows microphone permissions.
- Confirm that the correct audio input/output devices are selected.
- Check Unity audio settings.

## Testing Strategy

The project uses several levels of testing:

- **Component testing** - Individual motor, IMU and serial functions.
- **Integration testing** - Arduino-to-Unity communication and digital eye response.
- **System testing** - End-to-end operation of the physical and digital systems.
- **Evaluation testing** - Latency, positional synchronisation and IMU stability measurements.

The detailed experimental methodology and results are documented in the dissertation.

## Development and Extension

The main extension points are:

1. **Additional BPPV sequences** - Extend the Arduino state machine.
2. **Additional patient fields** - Update `Patient.cs` and the JSON data structure.
3. **Additional instructions** - Extend the instruction definitions in `InstructionManager`.
4. **Motor calibration** - Update the calibration data for the relevant PCA9685 channels.

## Limitations and Notes

- The system was developed and tested primarily on Windows 10/11.
- The physical platform and digital model require calibration before positional comparisons are made.
- Servo response and mechanical accuracy depend on the physical hardware and power supply.
- TTS/STT functionality depends on the host computer's audio configuration.
- The system is intended as a research and training platform; clinical diagnostic validity has not been established by this project.
- Hardware limits should be maintained to prevent the eye mechanism from being driven beyond its safe mechanical range.

## Future Work

Possible extensions include:

- Supporting additional BPPV canal variants.
- Increasing the available torsional mechanical range.
- Evaluating trainee learning outcomes.
- Developing an alternative interface for training and demonstration.

## Project Status

- **Project:** Dissertation research project.
- **Platform:** Arduino + Unity.
- **Primary target:** Windows 10/11.
- **Physical system:** Robotic head with six servo-driven eye axes.
- **Main application:** BPPV/Dix-Hallpike simulation and research evaluation.
