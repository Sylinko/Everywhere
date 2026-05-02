param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    # Optional: override which language subfolders to process. Defaults to auto-detected culture folders.
    [string[]]$LanguageFolders
)

$ErrorActionPreference = "Stop"

$publishRoot = Resolve-Path -Path $PublishDir
$entries = @(
  "libSkiaSharp.pdb",
  "PresentationCore.dll",
  "PresentationFramework.dll",
  "PresentationNative_cor3.dll",
  "PresentationUI.dll",
  "System.Windows.Forms.Primitives.dll",
  "System.Windows.Forms.dll",
  "wpfgfx_cor3.dll",
  "*/Microsoft.VisualBasic.Forms.resources.dll",
  "*/PresentationCore.resources.dll",
  "*/PresentationFramework.resources.dll",
  "*/PresentationUI.resources.dll",
  "*/System.Windows.Forms.Design.resources.dll",
  "*/System.Windows.Forms.Primitives.resources.dll",
  "*/System.Windows.Forms.resources.dll",
  "*/WindowsFormsIntegration.resources.dll"
)

$allFiles = Get-ChildItem -Path $publishRoot -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName

if (-not $allFiles) {
    Write-Host "No files found under publish root: $publishRoot"
    exit 0
}

$targets = @()

foreach ($entry in $entries) {
    # normalize slashes to backslashes
    $norm = $entry -replace '/','\'
    # build full-path pattern
    $fullPattern = Join-Path -Path $publishRoot -ChildPath $norm

    if ($norm -like '*[*?]*') {
        # pattern contains wildcard(s) -> use -like against full paths
        $matches = $allFiles | Where-Object { $_ -like $fullPattern }
    } else {
        # literal path -> exact match against full paths
        $literalPath = $fullPattern
        $matches = @()
        if ($allFiles -contains $literalPath) { $matches += $literalPath }
    }

    if ($matches) {
        $targets += $matches
    } else {
        Write-Host "Pattern matched nothing: $entry" -ForegroundColor DarkGray
    }
}

$targets = $targets | Sort-Object -Unique

foreach ($path in $targets) {
    if (Test-Path -LiteralPath $path) {
        Write-Host "Removing $path"
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "Skip (not found): $path" -ForegroundColor DarkGray
    }
}
