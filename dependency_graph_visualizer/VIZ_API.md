# vizcore / viz_cli / viz_mcp — анализатор зависимостей как инструмент агента

Индекс связей C#/GDScript/сцен проекта: кто что вызывает, что затронет правка,
циклы, конкурентность, горячий путь, покрытие тестами, дрейф документации.
Только stdlib, Python 3.10+.

## CLI (JSON на выходе)

```bash
# сводка
python viz_cli.py stats --repo "D:/bebebe-main (1)/bebebe-main"

# кто вызывает символ / что вызывает символ
python viz_cli.py callers --symbol RegisterJob
python viz_cli.py callees --symbol JobDispatcher

# что затронет правка файла (прямые + транзитивные, разбивка по слоям)
python viz_cli.py impact --file src/simulation/jobs/JobSpatialPyramid.cs

# что затронет НЕЗАКОММИЧЕННОЕ изменение
python viz_cli.py impact-diff --base HEAD

# циклы зависимостей (SCC файлового графа)
python viz_cli.py cycles --scope src/simulation

# кто пишет в состояние
python viz_cli.py mutators --state _globalCdCount

# анализы
python viz_cli.py deadcode      # файлы/типы без входящих ссылок, сцены без ссылок
python viz_cli.py dangling      # .tscn -> несуществующие ресурсы, сигналы без метода
python viz_cli.py concurrency   # статические синглтоны, lock/Interlocked, потоки
python viz_cli.py hotpath       # методы из entry-point'ов кадра + оценка стоимости
python viz_cli.py layers        # зависимости снизу вверх (нарушения слоёв)
python viz_cli.py coverage      # покрытие src тестами, непокрытые горячие файлы
python viz_cli.py plandrift     # ссылки PLAN.md на несуществующие файлы/строки

# слежение за правками с пересчётом impact
python viz_cli.py watch --interval 5
```

Флаги: `--repo PATH` (по умолчанию `../bebebe-main`), `--pretty`, `--rebuild`.
Коды возврата: 0 — успех, 2 — ошибка аргументов, 3 — внутренняя ошибка.

## MCP-сервер (для агентов)

```bash
python viz_mcp.py --repo "D:/bebebe-main (1)/bebebe-main"
python viz_mcp.py --selftest        # проверка протокола
```

Инструменты: `viz_callers, viz_callees, viz_impact, viz_impact_diff, viz_cycles,
viz_mutators, viz_deadcode, viz_dangling, viz_concurrency, viz_hotpath, viz_layers,
viz_coverage, viz_plandrift, viz_stats, viz_symbol`.

Транспорт — stdio, по одному JSON-RPC 2.0 объекту на строку.

## Что важно знать о точности

- Рёбра строятся только по **однозначным** символам: если метод с таким именем
  объявлен в нескольких файлах, ребро не создаётся (иначе граф «липнет» и SCC
  схлопывается в половину проекта). Связей меньше, но каждая — настоящая.
- `impact` по умолчанию считает глубину 3. Если файл входит в цикл сильной
  связности, в ответе будет `in_cycle: true` и `scc_size` — транзитивный охват
  такого файла завышен по построению.
- `deadcode` не учитывает автолоады и сцены (на них нет входящих рёбер, но они
  нужны движку).
- Кэш: `.viz_cache.json` в корне репозитория, инвалидируется по mtime+size.

## Тесты

```bash
python -m unittest test_vizcore -v
```

15 проверок против реального репозитория (включая кэш, циклы, impact, анализы).
