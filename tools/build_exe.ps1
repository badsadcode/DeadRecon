# Builds the playable game: build\DeadRecon.exe (+ its .pck, .NET data folder, and the baked world beside it).
#   powershell -ExecutionPolicy Bypass -File tools\build_exe.ps1
# Needs: the Godot 4.7.2 .NET editor (path below) with its export templates, and a baked world in world\jungle.
param([string]$Godot = "F:\Codex\Project9a\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe")

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path $Godot)) { throw "Godot not found at $Godot" }
if (-not (Test-Path "world\jungle\world.json")) { throw "No baked world: dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json" }

Remove-Item -Recurse -Force build -ErrorAction SilentlyContinue
New-Item -ItemType Directory build | Out-Null
& $Godot --headless --path . --export-release "Windows Desktop" build/DeadRecon.exe
if (-not (Test-Path "build\DeadRecon.exe")) { throw "Export failed (see above)." }

# The baked world is read from disk next to the EXE (it is not packed into the .pck).
New-Item -ItemType Directory build\world | Out-Null
Copy-Item -Recurse world\jungle build\world\jungle
Remove-Item -Recurse -Force build\world\jungle\tiles_raw -ErrorAction SilentlyContinue
Write-Host "Done: $root\build\DeadRecon.exe"
