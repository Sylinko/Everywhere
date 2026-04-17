param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [string]$PruneListFile = "DeleteAfterPublish.txt",

    # Optional: override which language subfolders to process. Defaults to auto-detected culture folders.
    [string[]]$LanguageFolders
)

$ErrorActionPreference = "Stop"

function Resolve-ListPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path -Path $PSScriptRoot -ChildPath $Path
}

$publishRoot = Resolve-Path -Path $PublishDir
$listPath = Resolve-ListPath -Path $PruneListFile

if (-not (Test-Path -LiteralPath $listPath)) {
    throw "Prune list file not found: $listPath"
}

# Read entries, ignore blank lines and lines starting with #
$entries = Get-Content -LiteralPath $listPath |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object { $_.Trim() }

if (-not $entries) {
    Write-Host "Prune list is empty. Nothing to delete."
    exit 0
}

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
