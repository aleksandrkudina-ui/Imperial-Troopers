# Сборка Imperial Troopers 0.3.4 без Replit и Unity

В архив добавлен workflow `.github/workflows/build.yml`. Он запускает сборку на Windows runner в GitHub Actions, проверяет наличие DLL, модели и аудио, затем создаёт `ImperialTroopers_0.3.4_build.zip` и публикует его вместе с DLL как workflow artifacts.

## Что уже есть
- Исходники Imperial Troopers 0.3.4;
- `Stormtrooper.imodel` и исходный `Stormtrooper.fbx`;
- MP3-звуки;
- локальные reference DLL для BepInEx/R2API;
- workflow для удалённой сборки.

## Что ещё не подтверждено
Этот архив сам по себе **не содержит собранную DLL**. Сборка запускается только после загрузки содержимого архива в GitHub-репозиторий и запуска Actions. Успешная компиляция не означает, что мод проверен в самой игре; после сборки требуется игровой тест и анализ `BepInEx/LogOutput.log`.

## Запуск
1. Создай пустой GitHub repository.
2. Распакуй архив и загрузи всё содержимое в корень репозитория, сохранив `.github/workflows/build.yml`.
3. Открой вкладку **Actions** и запусти **Build Imperial Troopers** (или дождись автоматического запуска после push).
4. Если workflow завершился успешно, открой завершённый запуск и скачай artifact `ImperialTroopers-0.3.4-build`.
5. Для теста установи содержимое `ImperialTroopers_0.3.4_build.zip` в `BepInEx/plugins/ImperialTrooper/` согласно обычной структуре плагина.

Не добавляй в репозиторий пароли, токены, `.env` или другие секреты.
