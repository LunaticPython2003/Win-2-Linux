$cfg = Get-Content -Path D:\win2linux\esp-stage\EFI\BOOT\grub.cfg -Raw
$cfg = $cfg -replace 'rd\.live\.image(?!\s+rd\.live\.ram=1)', 'rd.live.image rd.live.ram=1'
Set-Content -Path D:\win2linux\esp-stage\EFI\BOOT\grub.cfg -Value $cfg
Set-Content -Path D:\win2linux\esp-stage\EFI\fedora\grub.cfg -Value $cfg

