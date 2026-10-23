# 🚀 RoboCopyUI

<div align="center">

<img src="Assets/app_icon.png" alt="RoboCopyUI Logo" width="128" height="128" />

### *Next-Gen, Ultra-Fast Multi-Threaded File Transfer GUI for Windows*

[![Peak Speed](https://img.shields.io/badge/Peak%20Velocity-2.0%20GB%2Fs-FF0055?style=for-the-badge&logo=speedtest&logoColor=white)](https://github.com/tusharrkabir/RoboCopyUI)
[![Max Threads](https://img.shields.io/badge/Multi--Threading-Up%20to%20128%20Threads-00E5FF?style=for-the-badge&logo=cpu&logoColor=white)](https://github.com/tusharrkabir/RoboCopyUI)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Version](https://img.shields.io/badge/Version-1.7.0-brightgreen?style=for-the-badge)](https://github.com/tusharrkabir/RoboCopyUI)
[![License](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](LICENSE)

<br/>

**RoboCopyUI** transforms Windows' enterprise-grade command-line tool (`robocopy.exe`) into an ultra-sleek, modern cyberpunk desktop utility. Built to unleash the raw, unthrottled read/write bandwidth of modern NVMe SSDs and destroy the notorious Windows Explorer slowdowns on millions of tiny files.

</div>

---

## ⚡ Peak Velocity Showcase (2.0 GB/s)

> **5 GB transferred in ~2 seconds flat!** Direct disk saturation achieving sustained **2.0 GB/s transfer speed** with real-time dynamic countdown ETA.

<div align="center">
  <img src="docs/screenshots/hero-speed-2gbs.png" alt="RoboCopyUI Blazing at 2.0 GB/s" width="94%" />
</div>

<br/>

<div align="center">
  <img src="docs/screenshots/complete-5gb-2s.png" alt="RoboCopyUI 5GB in 2s Complete" width="94%" />
</div>

---

## 📊 Dual Real-World Benchmarks

RoboCopyUI is engineered to conquer the two most common file transfer challenges:

### 1. 🏎️ Benchmark 1: Large File Raw Velocity (5 GB Transfer)
- **File Size:** 5.0 GB (Large video/archive file)
- **Sustained Speed:** **1.9 GB/s – 2.0 GB/s**
- **Completion Time:** **~2 seconds!**
- **Key Advantage:** Utilizes `/J` unbuffered direct I/O, bypassing Windows OS cache overhead and pumping data directly to NVMe storage.

### 2. ⚡ Benchmark 2: The Small-Files Stress Test (50,000+ Fragmented Files)
Anyone who has copied `node_modules`, Python virtual environments, or Git repositories knows Windows Explorer takes **an eternity** (dropping to 500 KB/s or freezing).

<div align="center">
  <img src="docs/screenshots/copying-active.png" alt="50,000 Files Multi-Threaded Transfer at 214 MB/s" width="90%" />
</div>

- **Workload:** **50,000+ small fragmented files** (3.2 GB total)
- **Threads:** **MT: 96 parallel worker threads**
- **Peak Burst:** **214.2 MB/s**
- **Average Velocity:** **95.4 MB/s**
- **Total Time:** **Just 34 seconds!** *(Standard Windows Explorer takes 20+ minutes on this same folder)*

<div align="center">
  <img src="docs/screenshots/copy-complete.png" alt="50,000 Files Completed in 34 Seconds" width="90%" />
</div>

---

## 🎨 Themes & Customization

RoboCopyUI includes an integrated theme engine supporting **Midnight (Cyber Dark)**, **Clean Light**, and **Classic Dark** modes:

| 🌌 Midnight Theme | ☀️ Clean Light Theme | 🌙 Classic Dark Theme |
| :---: | :---: | :---: |
| <img src="docs/screenshots/settings-midnight.png" alt="Midnight Theme" width="100%" /> | <img src="docs/screenshots/settings-light.png" alt="Light Theme" width="100%" /> | <img src="docs/screenshots/settings-dark.png" alt="Dark Theme" width="100%" /> |
| *High-contrast cyberpunk glow* | *Modern, clean daytime aesthetics* | *Sleek, deep neutral dark mode* |

---

## 🖱️ Native Windows Explorer Right-Click Integration

RoboCopyUI integrates directly into the Windows Shell context menu with custom branded icons. You don't need to manually open an app or type command prompt syntax—just right-click any file, folder, or directory:

<div align="center">
  <img src="docs/screenshots/context-menu.png" alt="Windows Explorer Right-Click Context Menu Integration" width="360px" />
</div>

- **📋 RoboCopy:** Accelerate file/folder copy operations with multi-threading into clipboard.
- **✂️ RoboCut:** High-velocity cut and move with atomic cleanup after successful transfer.
- **📥 RoboPaste:** Direct paste into any folder powered by the Robocopy engine.
- **🗑️ Robocopy Delete:** Instant turbo directory purge for massive folders (deletes 100,000+ files in seconds without Windows Explorer freezing).

---

## 🔥 Comparison: Windows Explorer vs RoboCopyUI

| Feature | Standard Windows Copy | 🚀 RoboCopyUI |
| :--- | :---: | :---: |
| **Peak Large File Speed** | ~300 – 600 MB/s | ⚡ **Up to 2.0 GB/s (Full drive bandwidth saturation)** |
| **Concurrency** | ❌ Single-threaded (1 file at a time) | ⚡ **Up to 128 Parallel Worker Threads (`/MT:128`)** |
| **50,000 Small Files (`node_modules`)** | 🐢 **Takes an eternity (drops to KB/s)** | 🚀 **Finished in 34 seconds at 95 – 214 MB/s!** |
| **Hardware Drive Auto-Tuning** | ❌ None | 🤖 **Auto-detects NVMe, SATA SSD, HDD, Network SMB** |
| **Multi-GB Large Files** | ⚠️ Floods OS RAM cache | 🏎️ **Large File Optimization (`/J` Unbuffered Direct I/O)** |
| **Locked / In-Use Files** | ❌ Halts entire transfer with modal dialog | 🛡️ **Zero-halt retry engine (`/R:0 /W:0` customizable)** |
| **Folder Purging / Deletion** | ⏳ Explorer freezes on deep directories | 🗑️ **Turbo RoboDelete (purges 100,000s files in seconds)** |
| **Visual Telemetry** | 📉 Fluctuating, unreliable estimate | 📊 **Live MB/s & GB/s speed with real-time ETA** |
| **Explorer Context Menu** | Standard options | 🖱️ **Native Right-Click: RoboCopy, RoboCut, RoboPaste & Robocopy Delete** |

---

## ✨ Key Features Breakdown

### 1. 🚀 Extreme Multi-Threading (Up to 128 Threads)
- Configure anywhere from **1 to 128 parallel worker threads** (`/MT`).
- Small files, deep folder hierarchies, and fragmented assets are transferred concurrently instead of waiting sequentially.
- **Smart Auto-Tune**: Automatically detects drive hardware type (PCIe NVMe SSD, SATA SSD, Spinning HDD, or Network SMB share) and applies the optimal thread ceiling to prevent disk thrashing.

### 2. 🏎️ Large File Optimization (`/J` Unbuffered I/O)
- Switches to unbuffered direct I/O for multi-GB files (ISOs, virtual disks, 4K raw video).
- Bypasses the Windows file system cache directly to physical storage, preserving system RAM and avoiding cache thrashing.

### 3. 🛡️ Fault Tolerance & Custom Retry Rules (`/R` & `/W`)
- Configure exact retry attempts and wait delay intervals for locked, busy, or permission-restricted files.
- Never wake up to a transfer that halted at 3 AM because a single temp file was open in another app.

### 4. 🗑️ Turbo Fast Delete (RoboDelete)
- Deleting deep directory trees in Windows Explorer can take 20+ minutes while Explorer calculates size.
- RoboCopyUI uses the **Robocopy Mirror Purge** technique to eradicate hundreds of thousands of files and directories in seconds.

### 5. 🔍 Interactive Skipped Files Inspector
- Keep track of every file skipped with live classification:
  - *Access Denied*
  - *File In Use*
  - *Corrupted File*
  - *Filtered by Pattern*

### 6. ⚔️ Collision Scanner & Smart Renaming
- Pre-scans source and destination to detect file collisions before starting.
- Options for automatic collision renaming, overwriting, or skipping.

### 7. 🖱️ Windows Explorer Right-Click Context Menu
- Full shell integration (`ShellRegistry.cs`) directly inside Windows Explorer.
- Right-click any file, folder, or drive to instantly perform **RoboCopy**, **RoboCut**, **RoboPaste**, or **Robocopy Delete** with custom shell icons.

---

## 🛠️ Architecture & Under the Hood

```
┌────────────────────────────────────────────────────────┐
│                   RoboCopyUI (WPF)                     │
│    MainWindow.xaml / SettingsWindow.xaml / Themes      │
└───────────────────────────┬────────────────────────────┘
                            │
       ┌────────────────────┼────────────────────┐
       ▼                    ▼                    ▼
┌──────────────┐   ┌─────────────────┐   ┌────────────────┐
│ PreScan &    │   │ RobocopyEngine  │   │  RoboDelete    │
│ Collision    │   │  (Process I/O   │   │  Runner        │
│ Detection    │   │  Streaming)     │   │  (Fast Purge)  │
└──────────────┘   └────────┬────────┘   └────────────────┘
                            │ Real-time Stdout Stream
                            ▼
           ┌────────────────────────────────┐
           │      Telemetry & Parsing       │
           │  • StdoutParser.cs             │
           │  • AccurateProgressTracker.cs  │
           │  • SpeedCalculator.cs          │
           │  • EtaEstimator.cs             │
           └────────────────────────────────┘
```

---

## 💻 Tech Stack & Requirements

| Component | Specification |
| :--- | :--- |
| **Operating System** | Windows 10 (Build 19041+) / Windows 11 (64-bit) |
| **Runtime Target** | [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (`net10.0-windows`) |
| **UI Framework** | Windows Presentation Foundation (WPF) + Windows Forms interop |
| **IDE Supported** | Visual Studio 2022 (v17.12+) / JetBrains Rider / VS Code |
| **Engine** | Microsoft Windows Native `robocopy.exe` |

---

## 🚀 Getting Started for Developers

### 1. Clone the Repository
```bash
git clone https://github.com/tusharrkabir/RoboCopyUI.git
cd RoboCopyUI
```

### 2. Open in Visual Studio
Open `RoboCopyUI.slnx` or `RoboCopyUI.csproj` in **Visual Studio 2022**.

*Ensure the **.NET Desktop Development** workload is installed.*

### 3. Build & Run via CLI
```powershell
# Restore & build
dotnet build

# Launch the application
dotnet run --project RoboCopyUI.csproj

# Launch Settings Window directly
dotnet run --project RoboCopyUI.csproj --settings
```

### 4. Publish a Portable Executable
To generate a single-file, self-contained executable:
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## 📁 Project Structure

```plaintext
RoboCopyUI/
├── Assets/                 # Application icons and branding graphics
├── core/                   # Core business logic & native system services
│   ├── ClipboardManager.cs # Clipboard monitoring & folder path handling
│   ├── DriveTypeDetector.cs# Identifies SSD, HDD, USB, Network drives for thread tuning
│   ├── NotificationService.cs # Native toast notifications
│   ├── RoboDeleteRunner.cs # Superfast directory deletion via robocopy mirror trick
│   ├── RobocopyEngine.cs   # Robocopy process management & command builder
│   ├── ShellRegistry.cs    # Windows Explorer context menu registration
│   ├── ThemeManager.cs     # Dynamic runtime theme switcher
│   └── WindowBlurHelper.cs # Acrylic / Mica backdrop blur effects
├── models/                 # Data contracts, parsers & telemetry calculators
│   ├── AccurateProgressTracker.cs # High-fidelity progress synchronization
│   ├── CollisionScanner.cs # Pre-copy collision & overwrite scanner
│   ├── EtaEstimator.cs     # Exponential moving average ETA calculation
│   ├── RobocopyLineParser.cs # Real-time line-by-line CLI parser
│   └── SpeedCalculator.cs  # Multi-sample bandwidth speed calculation
├── Themes/                 # WPF XAML themes (Dark, Light, Midnight)
├── docs/screenshots/       # High-resolution real UI screenshots
├── App.xaml / App.xaml.cs  # Application entrypoint & CLI argument router
├── MainWindow.xaml (.cs)   # Primary dashboard UI and real-time visualizer
├── SettingsWindow.xaml(.cs)# Advanced configurations, thread tuning & presets
├── RoboCopyUI.csproj       # Project build definition (.NET 10 WPF)
├── RoboCopyUI.slnx         # Visual Studio Solution definition
└── .gitignore              # Ignores build artifacts (bin/obj), setups, and VS cache
```

---

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!
1. Fork the Repository
2. Create your Feature Branch (`git checkout -b feature/NewFeature`)
3. Commit your Changes (`git commit -m "Add some NewFeature"`)
4. Push to the Branch (`git push origin feature/NewFeature`)
5. Open a Pull Request

---

## 📄 License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for more information.

---

<div align="center">
Made with ❤️ by <a href="https://github.com/tusharrkabir"><b>Tushar</b></a> (MT Electronics)
</div>
