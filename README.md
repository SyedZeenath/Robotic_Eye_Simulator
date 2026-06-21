# Robotic Eye Movement Simulator for the Dix-Hallpike Test

## Overview

This is a dissertation project that implements a robotic eye system with Arduino hardware control and a Unity-based frontend interface. The system enables precise control and real-time visualization of robotic eye movements. This project focuses on simulating Benign Paroxysmal Positional Vertigo (BPPV), specifically modeling Posterior BPPV to aid in medical research and diagnosis. The current implementation provides a simulation of Posterior BPPV eye movements for educational and clinical applications.

## Project Structure

### `/eye_controller/`

Contains the Arduino firmware for controlling the robotic eye hardware.

- **eye_controller.ino** - Main Arduino sketch that handles eye movement control and communication with the frontend

### `/unity_frontend/`

Complete Unity project containing the visual interface and control logic.

- **Assets/** - Game assets including models, scripts, scenes, and prefabs
  - **Scripts/** - C# control scripts
  - **Scenes/** - Unity scenes for different views
  - **Prefab/** - Reusable prefabs for the head model and eye components
  - **Resources/** - Textures, materials, and other resources
  - **StreamingAssets/** - Data files and configuration
- **Packages/** - Unity package dependencies
- **ProjectSettings/** - Unity project configuration

## Requirements

### Hardware

- Arduino microcontroller with appropriate motor drivers
- Servo motors or stepper motors for eye movement
- Power supply compatible with motors and Arduino

### Software

- Arduino IDE (for firmware development)
- Unity 2022.x or later (for frontend development and building)
- .NET Framework (required for Unity C# scripting)

## Setup Instructions

### Arduino Setup

1. Install Arduino IDE
2. Open `eye_controller/eye_controller.ino`
3. Configure board and COM port settings
4. Upload to your Arduino device

### Unity Frontend Setup

1. Install Unity 2022.x or later
2. Open the `unity_frontend/` folder as a Unity project
3. Wait for Unity to import all assets and packages
4. Open the desired scene from `Assets/Scenes/`
5. Configure any necessary settings in ProjectSettings

## Building & Deployment

### Building the Standalone Application

1. In Unity, go to File → Build Settings
2. Add scenes to build
3. Select target platform (Windows, Mac, Linux)
4. Click Build and select output directory

### Flashing Arduino Firmware

1. Connect Arduino via USB
2. Select correct board type and COM port
3. Click Upload in Arduino IDE

## Project Features

- Real-time eye movement control
- 3D head and eye model visualization
- BPPV-focused head twin model
- Realistic skybox rendering
- Communication protocol between frontend and hardware controller

## File Structure

- `.sln` - Visual Studio solution file for C# development
- `.csproj` - C# project configuration
- `.ino` - Arduino firmware source code

## Notes

- This project is part of a dissertation on robotic eye systems, potentially related to BPPV (Benign Paroxysmal Positional Vertigo) applications
- The head model and eye controls are specifically designed for medical research or simulation purposes
- Ensure proper power management for motors to prevent hardware damage

## License

[Add appropriate license information]

## Contact & Support

For questions or issues related to this project, please refer to the dissertation documentation.
