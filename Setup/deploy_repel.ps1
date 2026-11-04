$log = 'D:\Users\xym\Desktop\软件绿化\MenuClient\Setup\deploy.log'
$src = 'D:\Users\xym\Desktop\软件绿化\MenuClient\RepelService\bin\Release\net48\RepelService.exe'
$dst = 'D:\Users\xym\Desktop\软件绿化\MenuClient\dist\repel\RepelService.exe'
$out = @()
$out += "start $(Get-Date)"
$out += (net stop Repel 2>&1 | Out-String)
Start-Sleep -Seconds 1
$out += "service status: $((Get-Service Repel -ErrorAction SilentlyContinue).Status)"
try { Copy-Item $src $dst -Force -ErrorAction Stop; $out += "copy OK, dst time=$(Get-Item $dst | Select -Exp LastWriteTime)" }
catch { $out += "copy FAIL: $_" }
$out += (net start Repel 2>&1 | Out-String)
$out += "done $(Get-Date)"
$out | Out-File $log -Encoding UTF8
