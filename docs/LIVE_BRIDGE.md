# LiveBridge — управление реальной игрой (для агента и для глаз человека)

Отдельный мост, написанный с нуля: запускает **настоящую игру окном**, показывает
оверлей действий агента на экране и складывает скриншоты на диск, чтобы агент
**смотрел** кадры, а не угадывал пиксели.

От старого `godot-bridge` отличается тем, что:
- скриншот кадро-синхронный (`await RenderingServer.frame_post_draw`) и **сохраняется в PNG**;
- есть кроп и увеличение (смотреть на игрока, а не на весь экран);
- есть вход в игру из меню и ожидание условий (`start`, `wait-until`);
- на экране виден HUD: какую команду сейчас выполняет агент, FPS, текущая сцена;
- нет жёсткого списка команд на стороне плагина — протокол открытый, команды в GDScript.

## Как запускать

```bash
python tools/live.py launch                        # окно 1280x720 в позиции 80,80
python tools/live.py launch --position 40,40 --resolution 1600x900
python tools/live.py status                        # сцена, fps, узлы, окно, лог команд
python tools/live.py start                         # нажать «Играть» в меню
python tools/live.py click --x 400 --y 300         # клик по экранным координатам
python tools/live.py look --zoom 2                 # скрин + зум + список UI
python tools/live.py shot --name eye --scale 2     # скрин на диск
python tools/live.py perf                          # fps, process/physics, draw calls
python tools/live.py wait-until --expr "Engine.get_frames_per_second() > 30"
python tools/live.py eval --code "Engine.get_frames_per_second()"   # одно выражение
python tools/live.py run --code "var s = 0\nfor i in range(10):\n\ts += i\nreturn s"
python tools/live.py stop                          # корректный выход
```

Скриншоты складываются в `shots/live/*.png` — путь возвращается в JSON и его можно
открыть любым просмотрщиком (агент открывает через read_image).

## Архитектура

| Часть | Файл | Роль |
|---|---|---|
| Сервер в игре | `addons/live_bridge/live_bridge.gd` | автолоад, TCP `127.0.0.1:9097`, построчный JSON, HUD, все команды |
| Клиент | `tools/live.py` | лаунчер + клиент + сохранение скринов на диск |

Протокол: `{"id":1,"cmd":"shot","args":{...}}` → `{"id":1,"ok":true,"data":{...}}`.

Команды: `ping/state/perf/shot/look/ui/tree/eval/run/click/move/key/text/wait/`
`wait_until/start_game/scene/hud/tile_to_screen/quit`.

## Почему запускается из терминала, а не через плагин

Godot пишет `user://` при старте. Под файловой песочницей эти записи блокируются и
движок падает с `signal 11` — поэтому раньше запуск шёл только через unconfined
subprocess service (плагин). При `danger-full-access` запуск из обычного терминала
работает и, что важнее, **окно принадлежит твоей десктоп-сессии** — его видно.

Если политика снова станет ограниченной — запускать игру вручную (или из редактора
по F5) и подключаться к уже запущенному инстансу: клиент просто коннектится к 9097.

## Ограничения

- Сетка `tile_to_screen` предполагает клетку 32 px — при смене размера тайлов править здесь.
- `eval` принимает только одиночное выражение; блоки — через `run`.
- HUD рисуется поверх игры и виден на скриншотах (это сделано намеренно: на кадре
  видно, что именно выполнялось в этот момент).
