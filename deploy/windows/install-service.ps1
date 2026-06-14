param(
  [string]$InstallDir = "$env:ProgramFiles\DesensitizeProxy",
  [string]$ServiceName = "DesensitizePrivacyProxy"
)

$exe = Join-Path $InstallDir "DesensitizeProxy.AspNetCore.exe"
if (!(Test-Path $exe)) {
  throw "Executable not found: $exe"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
  sc.exe stop $ServiceName | Out-Null
  sc.exe delete $ServiceName | Out-Null
}

sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "Desensitize Privacy Proxy" | Out-Null
sc.exe start $ServiceName | Out-Null
