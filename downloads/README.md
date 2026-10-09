# СТЕВАНplejer — изворни ZIP

[Преузми ZIP са изворним кодом и упутством](https://github.com/014sita-rgb/stevan-play-site/raw/refs/heads/stevanplejer-windows-source-20261009/downloads/STEVANplejer_Windows_Universal_Source.zip)

Распакуј ZIP у посебну фасциклу, затим прочитај `desktop/TEST-ON-YOUR-PC.md`.
За твој Windows 11 64-bit и PowerShell 7, после додавања осталих развојних
алата из упутства, покрени из распаковане фасцикле:

```powershell
./desktop/scripts/test-local-windows.ps1 -TestBothArchitectures
```

Исти код има x64 и x86 пакете; универзални инсталер бира Windows 10 x86/x64
или Windows 11 x64 на Intel/AMD рачунару. Windows 11 32-bit не постоји.
За рад инсталираног плејера PowerShell и Python нису потребни.

**Ово је изворни пакет за проверу, не завршно издање апликације.**
Компајлирање и 123 теста логике су прошли у Linux cloud окружењу. Стварна
Windows репродукција, инсталација, живи API титлови и AI синхронизација још
нису потврђени; ограничења су у `desktop/VALIDATION.md`. Готови EXE фајлови,
API кључеви, лична подешавања и GitHub Actions нису укључени.
