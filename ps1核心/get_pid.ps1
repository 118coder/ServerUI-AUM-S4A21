$ppid = (Get-WmiObject Win32_Process -Filter "ProcessId=$pid").ParentProcessId
[IO.File]::WriteAllText("$env:TEMP\dnf_bat.pid", "$ppid")
