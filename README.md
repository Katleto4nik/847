# ZipToJar

Віконце: перетягни ZIP з модом (або натисни і вибери) — отримаєш .jar поруч із ZIP.

## Що вміє
- ZIP з Gradle-проєктом (build.gradle + gradlew.bat) -> справжня збірка `gradlew build`, .jar копіюється поруч із ZIP.
  Потрібен JDK 21 (з javac) і інтернет при першій збірці.
- ZIP з готовою папкою мода (fabric.mod.json + класи/ресурси) -> просто пакується в .jar.
- Можна перетягнути ZIP прямо на ZipToJar.exe або вибрати кілька ZIP одразу.

## Збірка .exe на GitHub
1. Залий вміст цієї папки в репозиторій (разом з .github).
2. Actions -> Build ZipToJar.exe -> Artifacts -> ZipToJar.


## Fix for Gradle argument parsing

The converter now launches `org.gradle.wrapper.GradleWrapperMain` directly
with `ProcessStartInfo.ArgumentList`. This avoids Windows batch-file quoting
that previously passed `build --no-daemon --console=plain` as one task name.
