$cfg = Get-Content -Path D:\win2linux\esp-stage\EFI\BOOT\grub.cfg -Raw
$cfg = $cfg -replace 'inst\.ks=hd:LABEL=LINUXEFI:/win2linux/unattended/kickstart\.ks quiet rhgb', 'root=live:CDLABEL=LINUXEFI rd.live.image inst.ks=hd:LABEL=LINUXEFI:/win2linux/unattended/kickstart.ks quiet rhgb'
Set-Content -Path D:\win2linux\esp-stage\EFI\BOOT\grub.cfg -Value $cfg
Set-Content -Path D:\win2linux\esp-stage\EFI\fedora\grub.cfg -Value $cfg
