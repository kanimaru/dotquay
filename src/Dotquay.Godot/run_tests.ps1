# Runs the Dotquay plugin's GDScript test suite headlessly.
#
# The import pass is not optional: `class_name` globals live in .godot/global_script_class_cache.cfg,
# which only exists after Godot has scanned the project. Skipping it makes every typed reference in
# the tests fail to compile, which reads like a code error but is really a missing cache — a trap
# worth spending one extra engine start to avoid, in CI and locally alike.
#
#   ./run_tests.ps1 -Godot "E:\Godot\current\godot.exe"
#
# Saved UTF-8 *with* a BOM on purpose: Windows PowerShell 5.1 reads a BOM-less file as ANSI, which
# turns the em-dashes in the strings below into mojibake and the script into a parse error. pwsh 7
# doesn't care either way, so the BOM is the version that works everywhere.

param(
    [string]$Godot = "godot",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$projectPath = $PSScriptRoot

function Invoke-Godot {
    param([string[]]$GodotArgs, [string]$Label)

    $stdout = [System.IO.Path]::GetTempFileName()
    $stderr = [System.IO.Path]::GetTempFileName()

    $process = Start-Process -FilePath $Godot -ArgumentList $GodotArgs -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    # Reading .Handle is not a no-op: without -Wait, Start-Process hands back a Process object that
    # never caches the process handle, and .ExitCode then reads as $null forever — no matter how long
    # you wait. `$null -ne 0` is true, so every fully passing run reported failure. Touching .Handle
    # here is what makes the object hold on to the handle long enough to read an exit code from it.
    $null = $process.Handle

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$Label timed out after $TimeoutSeconds seconds."
    }

    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut   = (Get-Content $stdout -Raw -ErrorAction SilentlyContinue)
        StdErr   = (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)
    }

    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    return $result
}

# The import pass ends by loading an editor layout it will never show, and does not always exit on
# its own — its output is what matters, not its exit code, so a timeout here is expected, not a
# failure.
try {
    Invoke-Godot -GodotArgs @("--headless", "--path", $projectPath, "--import") -Label "Import" | Out-Null
} catch {
    Write-Host "Import pass did not exit cleanly; continuing if the class cache exists."
}

if (-not (Test-Path (Join-Path $projectPath ".godot/global_script_class_cache.cfg"))) {
    throw "The import pass produced no global class cache — the tests cannot resolve class_name types."
}

$run = Invoke-Godot -GodotArgs @("--headless", "--path", $projectPath, "--script", "tests/test_runner.gd") -Label "Tests"

Write-Host $run.StdOut
if ($run.StdErr) { Write-Host $run.StdErr }

# The runner prints its own verdict and sets the exit code; script errors only ever reach stderr, so
# both are checked. A parse error in a suite exits 0 from Godot's point of view but ran no tests.
if ($run.ExitCode -ne 0 -or ($run.StdErr -match "SCRIPT ERROR" -or $run.StdErr -match "Parse Error")) {
    exit 1
}

exit 0
