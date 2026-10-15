[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Platform,
    [switch]$Session,
    [Alias('Input')][switch]$NativeInput,
    [switch]$RecoveryProcess,
    [switch]$Benchmarks
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $projectRoot
try {
    foreach ($suite in @('Core', 'Rendering', 'Storage', 'Windows', 'App')) {
        Write-Host "Running $suite checks (no UI or injected input)."
        & dotnet run --project "tests/FloatCanvas.${suite}Tests" -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "$suite checks failed with exit code $LASTEXITCODE" }
    }
    if ($Platform) {
        Write-Host '开始原生平台测试：会显示自建测试窗口、切换前台及移动鼠标／注入输入。请先退出浮绘应用，期间不要操作电脑。' -ForegroundColor Yellow
        try {
            & dotnet run --project 'tests/FloatCanvas.PlatformTests' -c $Configuration -- --wait-for-activation
            if ($LASTEXITCODE -ne 0) { throw "Native input checks failed with exit code $LASTEXITCODE" }
            & dotnet run --project 'tests/FloatCanvas.PlatformTests' -c $Configuration -- --capture-only
            if ($LASTEXITCODE -ne 0) { throw "Native capture checks failed with exit code $LASTEXITCODE" }
        }
        finally { Write-Host '原生平台测试段已结束。测试进程会关闭自建窗口并尝试恢复鼠标和原前台窗口。' -ForegroundColor Yellow }
    }
    if ($Session) {
        Write-Host '运行真实会话集成检查：显示自建窗口，暂用并恢复剪贴板；不注入鼠标或键盘。'
        & dotnet run --project 'tests/FloatCanvas.SessionTests' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Session checks failed with exit code $LASTEXITCODE" }
    }
    if ($NativeInput) {
        Write-Host '开始生产画布输入验收：会显示自建窗口、移动鼠标并发送受 HWND 限制的真实绘图与快捷键输入。请先退出浮绘，期间不要操作电脑。' -ForegroundColor Yellow
        try {
            & dotnet run --project 'tests/FloatCanvas.InputTests' -c $Configuration
            if ($LASTEXITCODE -ne 0) { throw "Production input checks failed with exit code $LASTEXITCODE" }
        }
        finally { Write-Host '生产输入验收段已结束；测试清理自建窗口并尝试恢复鼠标和原前台窗口。' -ForegroundColor Yellow }
    }
    if ($RecoveryProcess) {
        Write-Host '开始跨进程恢复验收：只启动和终止本次创建的两个浮绘子进程，使用独立测试配置；可能显示欢迎窗口，不注入输入。'
        & dotnet build 'src/FloatCanvas.App/FloatCanvas.App.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "Recovery application build failed with exit code $LASTEXITCODE" }
        $recoveryApplication = Join-Path $projectRoot "src/FloatCanvas.App/bin/$Configuration/net10.0-windows/FloatCanvas.exe"
        & (Join-Path $PSScriptRoot 'Test-RecoveryProcess.ps1') -ApplicationPath $recoveryApplication
    }
    if ($Benchmarks) {
        & dotnet run --project 'tests/FloatCanvas.RenderingBenchmarks' -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Rendering benchmark failed with exit code $LASTEXITCODE" }
    }
    Write-Host 'All requested checks passed.'
}
finally { Pop-Location }
