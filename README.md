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

## Quick Install & Uninstall (One-Line, No Clone)

### Linux (`x64`)

**Install** (downloads the latest release archive, extracts to a temp folder, and runs `install.sh`):
```bash
curl -fsSL https://api.github.com/repos/Markism-JA/ScholarFlow/releases/latest | grep -o 'https://[^"]*linux-x64\.tar\.gz' | head -n1 | xargs -I{} sh -c 'd=$(mktemp -d) && curl -fsSL "{}" | tar -xz -C "$d" && "$d/install.sh" && rm -rf "$d"'
```

**Uninstall:**
```bash
curl -fsSL https://raw.githubusercontent.com/Markism-JA/ScholarFlow/master/scripts/uninstall-linux.sh | bash
```

### Windows (`x64` PowerShell)

**Install** (downloads the latest release zip, extracts to `%TEMP%`, and runs `install.ps1`):
```powershell
$r = irm https://api.github.com/repos/Markism-JA/ScholarFlow/releases/latest; $u = ($r.assets | ? name -like '*win-x64.zip')[0].browser_download_url; $z = "$env:TEMP\ScholarFlow.zip"; $d = "$env:TEMP\ScholarFlow-install"; irm $u -OutFile $z; Expand-Archive $z $d -Force; & "$d\install.ps1"; Remove-Item $z,$d -Recurse -Force
```

**Uninstall:**
```powershell
irm https://raw.githubusercontent.com/Markism-JA/ScholarFlow/master/scripts/uninstall-windows.ps1 | iex
```

> **Note:** Uninstalling removes the application binary, Playwright runtime, and desktop/menu shortcuts. Your Study Vault folder (Markdown files, PDFs, and `.scholarflow/scholarflow.db`) is preserved unless you delete it manually.

---

## Manual Build, Publish & Install (From Clone)

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/Markism-JA/ScholarFlow.git
cd ScholarFlow
```

### Publish Release Binaries (`linux-x64` & `win-x64`)

```bash
./scripts/publish.sh v1.2.0
```

This outputs self-contained single-file builds and archives to `dist/`:
- `dist/linux-x64/` & `dist/ScholarFlow-v1.2.0-linux-x64.tar.gz`
- `dist/win-x64/` & `dist/ScholarFlow-v1.2.0-win-x64.zip`

### Install / Uninstall from the Cloned Repo

**Linux:**
```bash
./scripts/install-linux.sh
# To uninstall:
./scripts/uninstall-linux.sh
```
Installs to `~/.local/share/scholarflow/`, symlinks `~/.local/bin/ScholarFlow`, and registers the desktop entry & SVG icon under `~/.local/share/`.

**Windows (PowerShell):**
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-windows.ps1
# To uninstall:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-windows.ps1
```
Installs to `%LOCALAPPDATA%\Programs\ScholarFlow\`, adds it to User `PATH`, and creates Start Menu and Desktop shortcuts.

---

## Development

Run the desktop app directly from source during development:

```bash
dotnet run --project src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj
```

Build and run the full test suite (using `--disable-build-servers -m:1` to avoid MSBuild server locks):

```bash
dotnet build ScholarFlow.slnx --disable-build-servers -m:1
dotnet test ScholarFlow.slnx --no-build --disable-build-servers
```

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
