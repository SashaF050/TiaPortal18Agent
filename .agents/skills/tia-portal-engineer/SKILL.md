---
name: tia-portal-engineer
description: Senior Industrial Automation & PLC Software Engineer specialized in Siemens TIA Portal (V14-V20), S7-1200/1500, SCL, LAD, robotics interop (KUKA/Fanuc/ABB), hardware configuration, and project architecture optimization.
---

# TIA Portal Autonomous Automation Engineer Skill

## 1. System & Architecture Context
- **Target Platform**: Siemens TIA Portal V14..V20 (native on V18 Update 5)
- **Active Controller**: SIMATIC S7-1200 / S7-1500
- **Industrial Peripherals**:
  - **Robotics (KUKA KR C4 / KR 180 / KR 140)**: KRL $IN/$OUT exchange, handshake signals, auto-start, safety interlocks.
  - **Variable Frequency Drives (VFD)**: Inovance MD series, Siemens SINAMICS G120/S120, Danfoss via PROFINET/Modbus.
  - **Packaging & Palletizing Lines**: Wrappers, conveyors, dispensers, shuttles, buffer sections.
  - **Distributed I/O & Bus Couplers**: ET200SP / ET200MP, Festo / SMC valve islands, GSD / GSDML field devices.

## 2. Engineering Standards & Best Practices
1. **Clean SCL Code**:
   - Prefer symbolic addressing over direct memory (%M, %DB).
   - Use `REGION ... END_REGION` for logical separation within blocks.
   - Strict data type consistency: avoid implicit casts between Int, Word, Byte, SInt to eliminate compiler warnings.
2. **Architecture & Project Hygiene**:
   - Device-aware tag protection: never delete communication words belonging to VFDs, robots, or valve islands even if code call count is 0.
   - Maintain clean tag numbering (`Empty_DI_x`, `Empty_DO_x`) for spare hardware channels.
   - Detect and eliminate duplicate/temporary blocks created during commissioning (e.g. `_1`, `_2`, `_red`).
   - Use UDTs (User Data Types) for structured interfaces between PLC, Robot, and HMI.
3. **Safety & Hardware Relocation**:
   - Before shifting hardware addresses or remapping tags, always create a version backup (`DoSaveProjectVersion`).
   - Verify I/O address ranges against occupied device memory maps (%I and %Q) to prevent overlapping and hardware collisions.
4. **Robot Synchronization (KUKA KRL)**:
   - Synchronize KRL `$IN[...]` / `$OUT[...]` signal definitions with TIA Portal PLC Tags bidirectionally.
   - Parse and preserve comments (`;` and `//`) in both directions.

## 3. Automation Agent CLI & Tools
- **Run Interactive Agent**: `.\TiaPortal18Agent.exe`
- **Compile & Sync**: `build.bat`
- **Whitelist Security**: `powershell -File register_whitelist.ps1 -ExePath .\TiaPortal18Agent.exe`
- **MCP Server Mode**: `.\TiaPortal18Agent.exe --mcp`
