# Русификатор NFSCO (Need for Speed: Carbon Online)
# Запуск: install.bat / uninstall.bat. Подробности - README.md
param(
    [string]$GameDir,
    [string]$RusZip,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Warn($t) { Write-Host "[!] $t" -ForegroundColor Yellow }
function Fail($t) { throw $t }

$BackupName = 'NFSCO_backup_before_rus'
$LangFiles = foreach ($l in 'ENGLISH', 'GERMAN') { foreach ($p in 'FRONTEND', 'GLOBAL', 'INGAME') { "$($l)_$p.BIN" } }

function Test-GameDir($d) {
    $d -and (Test-Path -LiteralPath (Join-Path $d 'NFSCO\files\nfsco\LANGUAGES\ENGLISH_GLOBAL.BIN'))
}

function Find-GameDir {
    if ($GameDir) {
        if (Test-GameDir $GameDir) { return (Resolve-Path -LiteralPath $GameDir).Path }
        Fail "В папке '$GameDir' не найдена игра с установленным NFSCO."
    }
    # скрипт лежит в папке игры или в её подпапке
    foreach ($c in $PSScriptRoot, (Split-Path $PSScriptRoot -Parent)) {
        if (Test-GameDir $c) { return $c }
    }
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Выберите папку с игрой Need for Speed Carbon (где лежат bootstrapper.exe и папка NFSCO)'
    $dlg.ShowNewFolderButton = $false
    while ($true) {
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Fail 'Папка с игрой не выбрана.' }
        if (Test-GameDir $dlg.SelectedPath) { return $dlg.SelectedPath }
        [System.Windows.Forms.MessageBox]::Show("В этой папке нет NFSCO (не найден NFSCO\files\nfsco).`nВыберите папку, где лежит bootstrapper.exe.", 'Русификатор NFSCO') | Out-Null
    }
}

function Test-RusZip($path) {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $names = $z.Entries | ForEach-Object { $_.FullName }
            return ($names -contains 'LANGUAGES/Russian_Global.bin') -and
                   ($names -contains 'MEMCARD/locale_russian.loc') -and
                   [bool]($names | Where-Object { $_ -like '*/LANGUAGE/RUS/FONT_NFS_BODY.dds' }) -and
                   [bool]($names | Where-Object { $_ -like '*/LANGUAGE/RUS/BUNS/GLOBALA.BUN' })
        } finally { $z.Dispose() }
    } catch { return $false }
}

function Find-RusZip {
    if ($RusZip) {
        if (Test-RusZip $RusZip) { return $RusZip }
        Fail "Файл '$RusZip' не похож на архив русификатора 2.7 (см. README)."
    }
    $dirs = @($PSScriptRoot, (Join-Path $env:USERPROFILE 'Downloads'), [Environment]::GetFolderPath('Desktop'))
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        $cands = Get-ChildItem -LiteralPath $d -Filter '*.zip' -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -match '(?i)carbon|nfsc|rus' } | Sort-Object LastWriteTime -Descending
        foreach ($c in $cands) { if (Test-RusZip $c.FullName) { return $c.FullName } }
    }
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = 'Выберите zip-архив русификатора 2.7 от Madevi (с субтитрами или без)'
    $dlg.Filter = 'ZIP-архив (*.zip)|*.zip'
    while ($true) {
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Fail 'Архив русификатора не выбран.' }
        if (Test-RusZip $dlg.FileName) { return $dlg.FileName }
        [System.Windows.Forms.MessageBox]::Show("Это не тот архив. Нужен zip-архив русификатора 2.7 от Madevi (с субтитрами или без, ссылка в README).", 'Русификатор NFSCO') | Out-Null
    }
}

function Assert-GameClosed {
    $p = Get-Process -Name 'nfsc', 'bootstrapper' -ErrorAction SilentlyContinue
    if ($p) { Fail 'Игра запущена. Закройте Need for Speed Carbon и запустите установку снова.' }
}

function Assert-Writable($dir) {
    $probe = Join-Path $dir ('.rus_write_test_' + [guid]::NewGuid().ToString('N'))
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item -LiteralPath $probe -Force }
    catch { Fail "Нет прав на запись в '$dir'. Запустите install.bat от имени администратора." }
}

function Set-Language($reg, $value) {
    $r = [IO.File]::ReadAllText($reg)
    $r = [regex]::Replace($r, '"language"\s*:\s*"[^"]*"', '"language": "' + $value + '"')
    [IO.File]::WriteAllText($reg, $r, (New-Object Text.UTF8Encoding $false))
}

function Get-Language($reg) {
    $m = [regex]::Match([IO.File]::ReadAllText($reg), '"language"\s*:\s*"([^"]*)"')
    if ($m.Success) { $m.Groups[1].Value } else { $null }
}

function Same-File($a, $b) {
    (Test-Path -LiteralPath $a) -and (Test-Path -LiteralPath $b) -and
    ((Get-FileHash -LiteralPath $a).Hash -eq (Get-FileHash -LiteralPath $b).Hash)
}

# ---------------------------------------------------------------------------

$log = Join-Path $PSScriptRoot 'install.log'
try { Start-Transcript -LiteralPath $log -Force | Out-Null } catch { }
try {
    $game = Find-GameDir
    $n = Join-Path $game 'NFSCO\files\nfsco'
    $b = Join-Path $game $BackupName
    $reg = Join-Path $game 'NFSCO\registry.json'
    Write-Host "Игра: $game"
    Assert-GameClosed

    # ---------------- удаление ----------------
    if ($Uninstall) {
        if (-not (Test-Path -LiteralPath $b)) { Fail "Бэкап не найден ($b). Русификатор не установлен или бэкап удалён - переустановите NFSCO." }
        Step 'Возврат оригинальных файлов NFSCO'
        foreach ($f in $LangFiles) {
            if (Test-Path -LiteralPath "$b\LANGUAGES\$f") { Copy-Item -LiteralPath "$b\LANGUAGES\$f" "$n\LANGUAGES\$f" -Force }
        }
        if (Test-Path -LiteralPath "$b\GLOBAL\GLOBALA.BUN") { Copy-Item -LiteralPath "$b\GLOBAL\GLOBALA.BUN" "$n\GLOBAL\GLOBALA.BUN" -Force }
        if (Test-Path -LiteralPath "$b\MEMCARD\LOCALE_ENGLISH.LOC") { Copy-Item -LiteralPath "$b\MEMCARD\LOCALE_ENGLISH.LOC" "$n\MEMCARD\LOCALE_ENGLISH.LOC" -Force }
        if ((Test-Path -LiteralPath "$b\language.txt") -and (Test-Path -LiteralPath $reg)) {
            Set-Language $reg ((Get-Content -LiteralPath "$b\language.txt" -Raw).Trim())
        }
        if (Test-Path -LiteralPath "$b\movie_links.txt") {
            foreach ($m in Get-Content -LiteralPath "$b\movie_links.txt") {
                if ($m) { Remove-Item -LiteralPath (Join-Path $game "MOVIES\$m") -Force -ErrorAction SilentlyContinue }
            }
        }
        Remove-Item -LiteralPath $b -Recurse -Force
        Write-Host ''
        Write-Host 'Русификатор удалён, оригинальные файлы NFSCO восстановлены.' -ForegroundColor Green
        return
    }

    # ---------------- установка ----------------
    Assert-Writable $n
    $zipPath = Find-RusZip
    Write-Host "Архив русификатора: $zipPath"
    if (-not (Test-Path -LiteralPath $reg)) {
        Warn 'Не найден NFSCO\registry.json. Запустите игру через bootstrapper.exe хотя бы раз, затем повторите установку.'
        Warn 'Установка продолжится, но язык игры нужно будет проверить (должен быть English US).'
    }

    Step 'Подготовка сборщика'
    $src = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\NfscRus.cs') -Raw -Encoding UTF8
    if (-not ('NfscRus.LangBuilder' -as [type])) {
        Add-Type -TypeDefinition $src -ReferencedAssemblies 'System.Web.Extensions' -Language CSharp
    }

    $work = Join-Path $env:TEMP ('nfsco_rus_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$work\LANGUAGES", "$work\hd", "$work\out" | Out-Null
    try {
        Step 'Распаковка русификатора'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($e in $z.Entries) {
                $dest = $null
                if ($e.FullName -like 'LANGUAGES/Russian_*.bin') { $dest = "$work\LANGUAGES\$($e.Name)" }
                elseif ($e.FullName -like '*/LANGUAGE/RUS/*.dds') { $dest = "$work\hd\$($e.Name)" }
                elseif ($e.FullName -like '*/LANGUAGE/RUS/BUNS/GLOBALA.BUN') { $dest = "$work\hd\GLOBALA.BUN" }
                elseif ($e.FullName -eq 'MEMCARD/locale_russian.loc') { $dest = "$work\locale_russian.loc" }
                if ($dest) { [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true) }
            }
        } finally { $z.Dispose() }

        # Бэкап: сохраняем файлы NFSCO, если сейчас стоят оригиналы (первая установка или NFSCO обновился)
        Step 'Бэкап оригинальных файлов NFSCO'
        New-Item -ItemType Directory -Force "$b\LANGUAGES", "$b\GLOBAL", "$b\MEMCARD" | Out-Null
        $langOriginal = (New-Object NfscRus.Charset "$n\LANGUAGES\ENGLISH_GLOBAL.BIN").Marker -eq 256
        if ($langOriginal) {
            foreach ($f in $LangFiles) { Copy-Item -LiteralPath "$n\LANGUAGES\$f" "$b\LANGUAGES\$f" -Force }
        } elseif (-not (Test-Path -LiteralPath "$b\LANGUAGES\ENGLISH_GLOBAL.BIN")) {
            Fail 'Языковые файлы NFSCO уже изменены, а бэкапа нет. Переустановите NFSCO (или восстановите папку NFSCO\files\nfsco\LANGUAGES) и повторите.'
        }
        if (-not [NfscRus.GlobalaBuilder]::IsPatched("$n\GLOBAL\GLOBALA.BUN")) {
            Copy-Item -LiteralPath "$n\GLOBAL\GLOBALA.BUN" "$b\GLOBAL\GLOBALA.BUN" -Force
        } elseif (-not (Test-Path -LiteralPath "$b\GLOBAL\GLOBALA.BUN")) {
            Fail 'GLOBALA.BUN уже изменён, а бэкапа нет. Переустановите NFSCO и повторите.'
        }
        if (-not (Same-File "$n\MEMCARD\LOCALE_ENGLISH.LOC" "$work\locale_russian.loc")) {
            Copy-Item -LiteralPath "$n\MEMCARD\LOCALE_ENGLISH.LOC" "$b\MEMCARD\LOCALE_ENGLISH.LOC" -Force
        }
        if ((Test-Path -LiteralPath $reg) -and -not (Test-Path -LiteralPath "$b\language.txt")) {
            $lang = Get-Language $reg
            if ($lang) { Set-Content -LiteralPath "$b\language.txt" -Value $lang -Encoding ascii }
        }

        Step 'Сборка языковых файлов'
        $vanilla = Join-Path $game 'NFSCO\files\vanilla\LANGUAGES'
        Write-Host ([NfscRus.LangBuilder]::Build("$b\LANGUAGES", $vanilla, "$work\LANGUAGES", (Join-Path $PSScriptRoot 'translations'), "$work\out"))
        Step 'Сборка GLOBALA.BUN с русскими HD-шрифтами'
        Write-Host ([NfscRus.GlobalaBuilder]::Build("$b\GLOBAL\GLOBALA.BUN", "$work\hd\GLOBALA.BUN", "$work\hd", "$work\out\GLOBALA.BUN"))

        Step 'Установка файлов'
        foreach ($f in $LangFiles) { Copy-Item -LiteralPath "$work\out\$f" "$n\LANGUAGES\$f" -Force }
        Copy-Item -LiteralPath "$work\out\GLOBALA.BUN" "$n\GLOBAL\GLOBALA.BUN" -Force
        Copy-Item -LiteralPath "$work\locale_russian.loc" "$n\MEMCARD\LOCALE_ENGLISH.LOC" -Force
        if (Test-Path -LiteralPath $reg) { Set-Language $reg 'English US' }
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Ролики: в русском издании они называются *_german_pal.vp6, а игра (English US) ищет *_english_ntsc.vp6
    Step 'Ролики'
    $listFile = "$b\movie_links.txt"
    $links = @()
    if (Test-Path -LiteralPath $listFile) { $links = @(Get-Content -LiteralPath $listFile) }
    $made = 0; $copied = 0
    $movies = Join-Path $game 'MOVIES'
    if (Test-Path -LiteralPath $movies) {
        foreach ($f in Get-ChildItem -LiteralPath $movies -Filter '*_german_pal.vp6') {
            $new = $f.Name -replace '(?i)_german_pal\.vp6$', '_english_ntsc.vp6'
            $target = Join-Path $movies $new
            if (Test-Path -LiteralPath $target) { continue }
            try { New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null; $made++ }
            catch { Copy-Item -LiteralPath $f.FullName $target; $copied++ }
            $links += $new
        }
    }
    $links | Where-Object { $_ } | Sort-Object -Unique | Set-Content -LiteralPath $listFile -Encoding ascii
    if ($made + $copied) { Write-Host "Добавлено роликов: $made ссылок, $copied копий" }
    else { Write-Host 'Ничего делать не нужно' }

    Write-Host ''
    Write-Host 'Готово! Русификатор установлен.' -ForegroundColor Green
    Write-Host 'Удаление: uninstall.bat'
} catch {
    Write-Host ''
    Write-Host "ОШИБКА: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Подробный лог: $log"
    $global:LASTEXITCODE = 1
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
