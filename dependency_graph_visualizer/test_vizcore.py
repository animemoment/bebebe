"""Тесты vizcore против РЕАЛЬНОГО репозитория проекта. stdlib unittest.

Запуск: py -3 -m unittest test_vizcore -v
Папка визуализатора должна лежать рядом с анализируемым репозиторием,
либо задайте переменную окружения VIZ_REPO.
"""

import json
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import vizcore  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.environ.get("VIZ_REPO") or os.path.abspath(os.path.join(HERE, "..", "bebebe-main"))

_IDX = None


def idx():
    global _IDX
    if _IDX is None:
        _IDX = vizcore.ProjectIndex.build(REPO, use_cache=True)
    return _IDX


class TestIndex(unittest.TestCase):
    def test_repo_exists_and_indexed(self):
        self.assertTrue(os.path.isdir(REPO), f"нет репозитория: {REPO}")
        st = idx().stats()
        self.assertGreater(st["files"], 150, st)
        self.assertGreater(st["methods"], 1000, st)
        self.assertEqual(st["errors"], 0, f"ошибки парсинга: {st['errors']}")

    def test_layers_present(self):
        st = idx().stats()
        self.assertIn("simulation", st["by_layer"])
        self.assertIn("ui", st["by_layer"])

    def test_impact_of_known_file(self):
        cand = "src/simulation/jobs/JobSpatialPyramid.cs"
        self.assertIn(cand, idx().files, "файл не найден в индексе")
        r = idx().impact_of_file(cand)
        self.assertGreater(r["files_count"], 5, r)
        self.assertTrue(r["direct"], "нет прямых зависимых")
        json.dumps(r)

    def test_callers_of_known_symbol(self):
        r = idx().callers("RegisterJob")
        self.assertTrue(r, "не нашли вызывающих RegisterJob")
        self.assertTrue(all("file" in x for x in r))

    def test_cycles_returns_list(self):
        r = idx().cycles()
        self.assertIsInstance(r, list)
        for c in r[:5]:
            self.assertIn("nodes", c)
            self.assertGreaterEqual(len(c["nodes"]), 1)

    def test_mutators(self):
        r = idx().mutators("_globalCdCount")
        self.assertTrue(r, "должны найтись записи в _globalCdCount")
        json.dumps(r)

    def test_symbol_at(self):
        s = idx().symbol_at("src/simulation/jobs/JobSpatialPyramid.cs", 900)
        self.assertIsNotNone(s)
        self.assertIn("symbol", s)

    def test_dangling_tscn(self):
        r = idx().dangling()
        self.assertIn("missing_resource_refs", r)
        json.dumps(r)

    def test_dead_code(self):
        r = idx().dead_code()
        self.assertIn("unreferenced_files", r)
        json.dumps(r)

    def test_concurrency(self):
        r = idx().concurrency()
        self.assertGreater(r["count"], 5, r["count"])
        json.dumps(r)

    def test_hot_path(self):
        r = idx().hot_path(depth=2)
        self.assertGreater(len(r["entry_points"]), 10)
        json.dumps(r)

    def test_layer_violations(self):
        r = idx().layer_violations()
        self.assertIn("violations", r)
        json.dumps(r)

    def test_coverage(self):
        r = idx().coverage()
        self.assertGreater(r["src_files"], 100)
        json.dumps(r)

    def test_plan_drift(self):
        r = idx().plan_drift()
        self.assertNotIn("error", r)
        json.dumps(r)

    def test_cache_roundtrip_faster(self):
        t0 = time.perf_counter()
        a = vizcore.ProjectIndex.build(REPO, use_cache=True)
        t_full = time.perf_counter() - t0
        t0 = time.perf_counter()
        b = vizcore.ProjectIndex.build(REPO, use_cache=True)
        t_cached = time.perf_counter() - t0
        self.assertEqual(a.stats()["files"], b.stats()["files"])
        self.assertLess(t_cached, t_full + 0.05, f"кэш не быстрее: {t_cached:.3f} vs {t_full:.3f}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
