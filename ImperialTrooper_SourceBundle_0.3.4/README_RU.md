# Imperial Troopers 0.3.4 — исправление запуска без Unity

Готовый игровой архив — `ImperialTroopers_0.3.4.zip`. Исходный проект здесь нужен только для разработки.

## Изменения 0.3.4
- Устранено падение `InitializeTrooper()` на `DeathRewards.set_goldReward` при запуске на установленной пользователем сборке Risk of Rain 2.
- Все три поля наград (`fallbackGold`, `expReward`, `spawnValue`) устанавливаются через reflection, чтобы избежать прямого доступа к полю `fallbackGold`, который вызвал `FieldAccessException` в логе.
- Сохранена загрузка `Stormtrooper.imodel` без Unity AssetBundle, процедурная анимация, аудио и настройки спавна.

## Сборка исходников

```bash
cd ImperialTrooper_SourceProject
dotnet build ImperialTrooperMod.csproj --configuration Release
```

Проект на `netstandard2.1`, с `RiskOfRain2.GameLibs 1.5.0-r.1`. После build DLL, звуки и `Stormtrooper.imodel` копируются MSBuild target-ом в `release/plugins/ImperialTrooper/`.

## Модель

`Assets/Stormtrooper.imodel` содержит 208 nodes, 19 554 vertices, 6 518 triangles, 52 skin bones. Исходный `Stormtrooper.fbx` можно конвертировать при необходимости:

```bash
python -m pip install assimp_py
python tools/convert_fbx_to_itmd.py ImperialTrooper_SourceProject/Assets/Stormtrooper.fbx ImperialTrooper_SourceProject/Assets/Stormtrooper.imodel
```

## Статус проверки

В исходной поставке заявлена успешная компиляция с 0 warnings и 0 errors. В этой исправленной копии нужно пересобрать DLL локально; компиляция здесь не выполнялась. В логе пользователя 0.3.3 подтвердилось успешное чтение `Stormtrooper.imodel`, после чего мод упал на DeathRewards accessor; исправление построено под эту конкретную ошибку. Саму игру из среды разработки запустить нельзя, поэтому после установки 0.3.4 нужно проверить следующий этап и прислать новый `BepInEx/LogOutput.log`.

## Атрибуция модели

“(RIGGED) Stormtrooper” by GekWyd, Sketchfab, CC BY 4.0: https://sketchfab.com/3d-models/rigged-stormtrooper-0a59c5ffa4a1476bac6d540b79947f31
