param([switch]$Test, [switch]$Benchmark, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
$output = Join-Path $projectRoot $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpf $_) }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output @references "/win32icon:$projectRoot\src\App.ico" "/resource:$projectRoot\src\App.ico,App.ico" "/resource:$projectRoot\src\Main.xaml,Main.xaml" "/win32manifest:$projectRoot\src\app.manifest" "/out:$output\QuietReader.exe" "$projectRoot\src\Core.cs" "$projectRoot\src\Main.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Copy-Item "$projectRoot\src\QuietReader.exe.config" "$output\QuietReader.exe.config" -Force
Copy-Item "$projectRoot\使用说明.md" "$output\使用说明.md" -Force
Write-Output "Built: $output\QuietReader.exe"
if ($Test) {
    & $compiler /nologo /target:exe /utf8output @references "/out:$output\CoreTests.exe" "$projectRoot\src\Core.cs" "$projectRoot\tests\CoreTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
    & "$output\CoreTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $compiler /nologo /target:exe /main:WpfTests /utf8output @references "/resource:$projectRoot\src\Main.xaml,Main.xaml" "/out:$output\WpfTests.exe" "$projectRoot\src\Core.cs" "$projectRoot\src\Main.cs" "$projectRoot\tests\WpfTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'WPF test build failed' }
    & "$output\WpfTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'WPF tests failed' }
}
if ($Benchmark) {
    & $compiler /nologo /target:exe /main:PerformanceTests /optimize+ /utf8output @references "/resource:$projectRoot\src\Main.xaml,Main.xaml" "/out:$output\PerformanceTests.exe" "$projectRoot\src\Core.cs" "$projectRoot\src\Main.cs" "$projectRoot\tests\PerformanceTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed' }
    & "$output\PerformanceTests.exe" | Tee-Object -FilePath "$projectRoot\tests\performance-latest.log"
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark checks failed' }
}
