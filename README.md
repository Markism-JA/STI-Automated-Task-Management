# ScholarFlow

**ScholarFlow** is a local-first desktop academic workspace built in C# and Avalonia UI (`net10.0`). It combines a structured Markdown + SQLite FTS5 task basin, an Evidence-Grounded PDF Quiz & Active Recall Studio, a Syllabus-aware Master PDF Compiler, a built-in PDF Reader with verbatim quote highlighting, and optional STI eLMS synchronization.

---

## Overview

- **Local-First & Portable Vault:** Every course, task, note, attachment, and quiz lives in your chosen local Study Vault folder as plain Markdown (`inbox.md`, `tasks.md`) backed by a fast SQLite + FTS5 index (`.scholarflow/scholarflow.db`).
- **Works With or Without STI eLMS:** Use ScholarFlow completely standalone with local courses and imported PDFs, or enable STI eLMS integration to sync assignments, deadlines, handouts, and announcements automatically.
- **Model-Agnostic AI + Offline Fallback:** Connect any OpenAI-compatible provider (Google AI Studio, OpenAI, OpenRouter, DeepSeek, Groq, Ollama, LM Studio, or custom endpoints), copy/paste prompts via the Manual JSON Bridge, or generate quizzes 100% offline with the built-in PDF analyzer.
- **Cross-Platform Desktop Integration:** Supports system tray minimization (`--minimized`), native desktop notifications, launch-on-startup, and keyboard-first navigation.

---

## Feature Showcase

### 1. Task & Course Workspace
- **Smart Views:** `Inbox`, `Today`, `Upcoming` (including due subtasks), `Calendar`, `Completed` (with per-subject filter bar), and `Courses`.
- **Flexible Grouping & Hierarchy:** Group tasks by Default, Term (`Prelims`, `Midterms`, `Prefinals`, `Finals`), Due Date, Priority (`P1`–`P4`), or Course, with nested subtasks, labels, instruction/submission attachments, and Markdown notes.
- **Quick Add & Global Search:** Press `Q` to quick-add tasks with natural tokens (`@course`, `#label`, `!p1`, `due:`), or `Ctrl+K` to search across courses, tasks, PDFs, and saved quizzes.

### 2. Evidence-Grounded Quiz & Active Recall Studio
- **3-Step Scope & Prompt Builder:** Target an entire Master PDF, a specific term, or individual handouts/page ranges with live visual PDF page previewing and reactive prompt preview.
- **Custom Question-Type Allocation:** Mix `Multiple Choice`, `Identification`, `Situational`, `Ordering`, and `Fill in the Blank` by percentage weights or exact counts.
- **Specialized Content Modalities (Auto-Detected):**
  - **Code Snippets:** Monospace code blocks with language badges and one-click `Copy Code`.
  - **Math / LaTeX:** Stacked visual fractions and mathematical Unicode typography with strict Multiple Choice / Situational enforcement.
  - **Flow Diagrams:** Connected node-flow cards parsed from `->` chains.
  - **Smart Detection:** Automatically avoids forcing code/math formats onto prose-only subjects.
- **Guarded Custom Prompt Directives:** Add focus/style instructions (e.g., exam-style scenarios, troubleshooting) inside isolated `<user_directives>` boundaries that preserve JSON schema and evidence-grounding rules.
- **Deck Organizer & Active Runner:** Reorder, edit, merge, or re-ground decks; pause and resume sessions (`Continue (Q X/Y)`) or `Restart`; review with SM-2/FSRS spaced repetition ratings and jump straight to the exact highlighted quote in the built-in PDF Reader.

### 3. Master PDF Compiler & Built-in PDF Reader
- **Syllabus Term Mapping:** Parses syllabus PDFs to map topics and handouts into `Prelims`, `Midterms`, `Prefinals`, and `Finals`, compiling course handouts into bookmarked Master PDFs.
- **Built-in PDF Reader:** Non-blocking async page rendering with caching and exact normalized `[0..1]` highlight bounding boxes for quiz evidence quotes.

### 4. Model-Agnostic AI Profiles
- Built-in presets for **Google AI Studio**, **OpenAI**, **OpenRouter**, **DeepSeek**, **Groq**, **Ollama**, **LM Studio**, and **Offline PDF Analyzer**.
- Save, switch, and manage multiple named `AiProviderProfile` configurations.

---

## Installation

Download the latest archive for your platform from the [GitHub Releases](https://github.com/Markism-JA/STI-Automated-Task-Management/releases) page.

### Linux (`x64`)

1. Extract the release archive:
   ```bash
   tar -xzf ScholarFlow-v1.2.0-linux-x64.tar.gz
   cd ScholarFlow-v1.2.0-linux-x64 || true
   ```
   *(If the archive extracts directly into the current directory, run `./install.sh` from where `ScholarFlow` was extracted.)*
2. Run the installer:
   ```bash
   chmod +x install.sh
   ./install.sh
   ```
   This installs:
   - Application & Playwright runtime: `~/.local/share/scholarflow/`
   - CLI symlink: `~/.local/bin/ScholarFlow`
   - Icon: `~/.local/share/icons/hicolor/scalable/apps/scholarflow.svg`
   - Desktop entry: `~/.local/share/applications/scholarflow.desktop`

### Windows (`x64`)

1. Extract `ScholarFlow-v1.2.0-win-x64.zip`.
2. Right-click `install.ps1` and choose **Run with PowerShell** (or run in PowerShell):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   This installs:
   - Executable & Playwright runtime: `%LOCALAPPDATA%\Programs\ScholarFlow\`
   - User `PATH` entry for `%LOCALAPPDATA%\Programs\ScholarFlow`
   - Start Menu and Desktop shortcuts (`ScholarFlow.lnk`)

### Build from Source

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/Markism-JA/STI-Automated-Task-Management.git
cd STI-Automated-Task-Management
dotnet build ScholarFlow.slnx --disable-build-servers -m:1
dotnet run --project src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj
```

To build self-contained single-file release archives for both Linux and Windows:

```bash
./scripts/publish.sh v1.2.0
```

---

## Uninstallation

Uninstalling removes the application binary, runtime, and desktop shortcuts. Your Study Vault folder (Markdown files, PDFs, and `.scholarflow/scholarflow.db`) is **preserved** unless you delete it manually.

### Linux

Run the included uninstaller:

```bash
./uninstall.sh
```

Or remove the installed files manually:

```bash
rm -rf ~/.local/share/scholarflow
rm -f ~/.local/bin/ScholarFlow
rm -f ~/.local/share/icons/hicolor/scalable/apps/scholarflow.svg
rm -f ~/.local/share/applications/scholarflow.desktop
```

### Windows

Run the included PowerShell uninstaller:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Or remove `%LOCALAPPDATA%\Programs\ScholarFlow`, along with the `ScholarFlow.lnk` shortcuts from the Start Menu (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\`) and Desktop.

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `1` – `6` | Switch views (`Inbox`, `Today`, `Upcoming`, `Calendar`, `Completed`, `Test & Recall`) |
| `Ctrl+K` | Global search (courses, tasks, PDFs, quizzes) |
| `Q` | Quick Add task modal |
| `S` | Toggle sidebar |
| `I` | Toggle task inspector |
| `Up` / `Down` | Navigate task list |
| `1` – `4` (in Quiz Runner) | Select Multiple Choice / Situational option |
| `Left` / `P`, `Right` / `N` | Previous / Next question in Quiz Runner |
| `Esc` | Close active modal or drawer |
