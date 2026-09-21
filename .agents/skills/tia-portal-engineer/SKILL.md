---
name: tia-portal-engineer
description: Senior Industrial Automation & PLC Software Engineer specialized in Siemens TIA Portal V18, S7-1200/1500, SCL, LAD, simulation, and project architecture optimization. Use whenever working with project SPS_Mechta_2026, writing or modifying PLC blocks, analyzing code quality, performing project audits, running simulations, or fixing compiler errors.
---

# TIA Portal V18 Senior Automation Engineer Skill (v2.5.0)

## 1. System & Architecture Context
- **Target Platform**: Siemens TIA Portal V18 (Update 5)
- **Active Controller**: SIMATIC S7-1200 / S7-1500
- **Agent Executable & Host Tools**: `C:\Users\aa.fedin\Favorites\Tia_18_Agent\`
- **Key Subsystems**:
  - **Conveyors Line**: 11_Conveyors_CoreBlocks, 12 Conveyors Integrated (Conveyors 101-105, 201-206, shuttles, buffer, spacing)
  - **Robotics (KUKA KR 180 / KR C4)**: 10_Robots (RobotControl_FC, Robot_AutoStart, ROBOT_CONTROL_DB, Receipts, ROBOT180_IN_DB, ROBOT180_OUT_DB)
  - **Drives & Inverters**: Inovance MD series (table INOVANCE) via Modbus/PROFINET
  - **Packaging / Pallet Wrapper**: Pieri Wrapper (table Pieri, Tags_PalletWrapper_DB)
  - **Pallet Storage / Dispenser**: Short rolls & dispenser (Tags_StorePallet_DB, Tags_Dispenser_DB, FAS_301_00BH11)
  - **Sensors & Remote IO**: FAS_20_007B11, FAS_30_00BE31, PLC_HARD, PLC_Module_1 DI, PLC_Module_2 DQ

---

## 2. Token Optimization Guidelines (Local Host Processing)
To minimize LLM token consumption by >90%:
1. **Never dump full blocks when only a section or network is needed**:
   - Use `tia_read_scl` with `networkNumber` (e.g. `read-scl Errors_FC 2`) to decompile ONLY the target network.
   - Use `tia_read_scl` with `outlineOnly` (e.g. `read-scl Errors_FC --outline`) to get a concise ~50-token summary of all networks with their titles.
   - Use `tia_read_block_interface` with `sectionFilter` (`Input`, `Output`, `InOut`, `Static`, `Temp`) to fetch only relevant variable declarations (~100 tokens vs 1500+).
2. **Search locally on the PC rather than loading all tags/blocks into context**:
   - Call `tia_search_blocks` to find blocks or UDTs locally on the PC by name, type, or folder.
   - Call `tia_search_tags` to query PLC tag tables by name, address (%I, %Q, %M), or comment.
3. **Resilient Inconsistent Block Handling**:
   - The agent automatically catches `Inconsistent blocks and PLC data types (UDT) cannot be exported` errors and triggers a compiler pass before reading.

---

## 3. Automation Agent Tools (tia-portal-v18)

### Connection, Archiving & Lifecycle
- `tia_list_processes`: Lists active TIA Portal instances and open project paths.
- `tia_connect`: Attaches to active TIA Portal instance.
- `tia_open_headless`: Starts headless TIA Portal process and opens project.
- `tia_get_project_info`: Metadata about active project.
- `tia_save_project`: Persists project changes.
- `tia_archive_project`: Fast compressed `.zap18` project archiving (`DiscardRestorableDataAndCompressed`), available via MCP, CLI (`archive` / `zap18`), and Interactive Dashboard key `[Z]`.
- `tia_retrieve_project`: Extracts and opens a `.zap18` archive directly into a target folder.

### Accelerated Search & Inspection
- `tia_search_blocks`: Fast local search across blocks & UDTs by name/type/path.
- `tia_search_tags`: Fast local search across tag tables by name/address/comment.
- `tia_read_block_interface`: Decompiles block/UDT interface into concise SCL declarations (supports `sectionFilter`).
- `tia_read_scl`: Decompiles SimaticML XML into readable SCL logic (supports `networkNumber` and `outlineOnly`).

### Block Authoring & Invocation
- `tia_create_block`: Creates and compiles new FC, FB, DB, or UDT from SCL.
- `tia_copy_block`: Cross-project copy of blocks (FC, FB, DB, OB) or UDTs with recursive dependency auto-copying.
- `tia_call_block`: Inserts a call to an FB or FC into a caller block:
  - **FB in FB (Multi-Instance / мультивызов)**: Declares instance in caller's `Static` section (`#inst_name : "Callee_FB"`), encapsulating DB inside the caller without global instance DBs.
  - **FB in FC/OB (Single-Instance)**: Generates single-instance DB.
  - **FC call**: Direct call `"FC_Name"(...)`.
  - Supports parameter mapping (e.g. `Error:=#Error,State:=#State`).
- `tia_get_device_params`: Reads controller IP, subnet, PROFINET device name, and modules.
- `tia_set_device_param`: Modifies IP address, subnet mask, or PN device name.
- `tia_add_device`: Adds new device (PLC, drive, HMI) from MLFB / catalog.
- `tia_add_module`: Plugs I/O module into rack slot.

### Diagnostics & Simulation
- `tia_compile`: Software/hardware compilation with detailed error/warning breakdown.
- `tia_audit_project`: Full structural audit (dead code, duplicate blocks).
- `tia_clean_garbage`: Programmatic safe removal of unreachable dead blocks.
- `tia_check_simulation` / `tia_start_simulation`: S7-PLCSIM V18 control.
- `tia_get_call_structure` / `tia_get_dependency_structure`: Native call and data dependency trees.
- `tia_get_memory_resources`: Work, load, and retentive memory utilization.
- `tia_get_hardware_config`: Controller rack interrogation.
