using System;
using Godot;
using Game.UI;

namespace Game.Tests;

/// <summary>
/// @destroyer (PLAN §117, задача 2f / находка F3): adversarial-тест ввода и камеры.
/// Симулируем ДОЛГИЙ КАДР (хитч террейна/стриминга → delta до 30 секунд) и проверяем:
///  - обычный кадр двигает камеру корректно (поллинг физической клавиши);
///  - один длинный кадр НЕ телепортирует камеру через полкарты (нет clamp delta = F3);
///  - Shift-ускорение на длинном кадре так же ограничено;
///  - без ввода и ПОСЛЕ отпускания клавиши камера не движется сама («призрачный ход» F3).
/// Инвариант: перемещение за один вызов _Process не превышает путь за 0.5с штатной скорости,
/// каким бы ни был delta. Проба инъекции клавиш в headless: Input.UseAccumulatedInput=false.
/// </summary>
public partial class TestDestroyerCamera : RefCounted
{
    /// <summary>Максимальный «путь» камеры за один кадр: 0.5 секунды штатной скорости.</summary>
    private const float MaxFrameSecondsBudget = 0.5f;

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[DESTROYER CAMERA] Starting adversarial camera/input audit (@destroyer)");
        GD.Print("==================================================");
        try
        {
            Input.SetUseAccumulatedInput(false);

            TestNormalFrameMovesCamera();
            TestLongFrameDoesNotTeleport();
            TestLongFrameWithShiftDoesNotTeleport();
            TestNoInputNoMovement();
            TestReleasedKeyNoGhostMovement();
            TestBoundsClampOutsideTreeIsSafe();

            GD.Print("==================================================");
            GD.Print("[DESTROYER CAMERA OK] ALL CAMERA ADVERSARIAL TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DESTROYER CAMERA FAILED] {ex.Message}\n{ex.StackTrace}");
            return false;
        }
        finally
        {
            ReleaseKey(Key.W); ReleaseKey(Key.S); ReleaseKey(Key.A); ReleaseKey(Key.D);
            ReleaseKey(Key.Up); ReleaseKey(Key.Down); ReleaseKey(Key.Left); ReleaseKey(Key.Right);
            ReleaseKey(Key.Shift);
        }
    }

    private static CameraController NewCamera()
    {
        // Вне дерева: _Ready не вызываем (InputMap/зум не нужны — тестируем только _Process).
        // ClampPosition вне дерева безопасен (guard IsInsideTree).
        var camera = new CameraController
        {
            MoveSpeed = 3500f,
            ShiftMultiplier = 2.5f,
            Position = Vector2.Zero,
            MinPositionTiles = new Vector2(-1_000_000f, -1_000_000f),
            MaxPositionTiles = new Vector2(1_000_000f, 1_000_000f),
            TileSize = 64,
        };
        return camera;
    }

    private static void PressKey(Key key) => InjectKey(key, true);
    private static void ReleaseKey(Key key) => InjectKey(key, false);

    private static void InjectKey(Key key, bool pressed)
    {
        var ev = new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = pressed,
        };
        Input.ParseInputEvent(ev);
    }

    private static void AssertNoPhysicalKeysPressed()
    {
        foreach (Key key in new[] { Key.W, Key.A, Key.S, Key.D, Key.Up, Key.Down, Key.Left, Key.Right, Key.Shift })
        {
            if (Input.IsPhysicalKeyPressed(key))
                throw new Exception($"Physical key {key} is stuck pressed before test start.");
        }
    }

    private void TestNormalFrameMovesCamera()
    {
        GD.Print("[CTEST 1] Normal frame (16 ms) with D held moves camera right...");
        AssertNoPhysicalKeysPressed();
        var camera = NewCamera();
        try
        {
            PressKey(Key.D);
            camera._Process(0.016);
            float dx = Math.Abs(camera.Position.X);
            float dy = Math.Abs(camera.Position.Y);
            // 3500 * 0.016 = 56 px; допуск широким на тайминги — но движение обязано быть.
            if (dx < 20f || dx > 200f)
                throw new Exception($"[P2] D+16ms frame moved camera by {dx} px (expected ~56).");
            if (dy > 1f)
                throw new Exception($"[P2] D-only movement shifted Y by {dy} px.");
            GD.Print($"   -> moved {dx} px right in one frame, OK.");
        }
        finally
        {
            ReleaseKey(Key.D);
            camera.Free();
        }
    }

    private void TestLongFrameDoesNotTeleport()
    {
        GD.Print("[CTEST 2] LONG frame (30 s, hitch) with D held must NOT teleport camera...");
        AssertNoPhysicalKeysPressed();
        var camera = NewCamera();
        try
        {
            float budget = camera.MoveSpeed * MaxFrameSecondsBudget; // 3500 * 0.5 = 1750 px
            PressKey(Key.D);
            camera._Process(30.0); // хитч: террейн/стриминг заморозил кадр на полминуты
            float dx = Math.Abs(camera.Position.X);
            GD.Print($"   -> delta=30s, displacement={dx} px, budget={budget} px");
            if (dx > budget)
                throw new Exception($"[P1][F3] CameraController._Process has no delta clamp: a single 30s frame " +
                    $"moved the camera {dx} px (> {budget} px budget). PLAN §117 F3: 'W на секунду — иду ещё 1.5с'. " +
                    "Fix: clamp delta (e.g. to 0.1s) before Position += dir * speed * delta.");
        }
        finally
        {
            ReleaseKey(Key.D);
            camera.Free();
        }
    }

    private void TestLongFrameWithShiftDoesNotTeleport()
    {
        GD.Print("[CTEST 3] LONG frame (15 s) with Shift+D must NOT teleport camera...");
        AssertNoPhysicalKeysPressed();
        var camera = NewCamera();
        try
        {
            float budget = camera.MoveSpeed * camera.ShiftMultiplier * MaxFrameSecondsBudget; // 4375 px
            PressKey(Key.Shift);
            PressKey(Key.D);
            camera._Process(15.0);
            float dx = Math.Abs(camera.Position.X);
            GD.Print($"   -> delta=15s Shift, displacement={dx} px, budget={budget} px");
            if (dx > budget)
                throw new Exception($"[P1][F3] Shift+long frame: camera moved {dx} px (> {budget} px). " +
                    "Unbounded delta * ShiftMultiplier amplifies hitches.");
        }
        finally
        {
            ReleaseKey(Key.Shift);
            ReleaseKey(Key.D);
            camera.Free();
        }
    }

    private void TestNoInputNoMovement()
    {
        GD.Print("[CTEST 4] LONG frame (60 s) with NO input must not move camera...");
        AssertNoPhysicalKeysPressed();
        var camera = NewCamera();
        try
        {
            camera._Process(60.0);
            if (camera.Position != Vector2.Zero)
                throw new Exception($"[P2] Camera moved without any input: {camera.Position}.");
            GD.Print("   -> zero movement, OK.");
        }
        finally
        {
            camera.Free();
        }
    }

    private void TestReleasedKeyNoGhostMovement()
    {
        GD.Print("[CTEST 5] Key released BEFORE long frame — no ghost movement after release (F3 repro)...");
        AssertNoPhysicalKeysPressed();
        var camera = NewCamera();
        try
        {
            PressKey(Key.W);
            camera._Process(0.016); // нормальный кадр — камера пошла вверх
            float afterPress = Math.Abs(camera.Position.Y);
            if (afterPress < 20f)
                throw new Exception("[P2] W press did not move camera in normal frame.");

            ReleaseKey(Key.W);
            camera._Process(30.0); // длинный кадр ПОСЛЕ отпускания
            float afterRelease = Math.Abs(camera.Position.Y);
            float drift = afterRelease - afterPress;
            GD.Print($"   -> after press: {afterPress} px, after release+30s frame: {afterRelease} px (drift {drift})");
            if (drift > 1f)
                throw new Exception($"[P1][F3] Ghost movement after key release: camera drifted {drift} px " +
                    "during a long frame with no keys held (user repro: 'W на секунду — иду ещё 1.5с').");
        }
        finally
        {
            ReleaseKey(Key.W);
            camera.Free();
        }
    }

    private void TestBoundsClampOutsideTreeIsSafe()
    {
        GD.Print("[CTEST 6] SetWorldBoundsTiles + ClampPosition outside tree are safe...");
        var camera = NewCamera();
        try
        {
            camera.Position = new Vector2(999999f, 999999f);
            camera.SetWorldBoundsTiles(0, 0, 100, 100); // вызовет ClampPosition (guard IsInsideTree)
            camera._Process(0.016);
            GD.Print("   -> no exceptions with out-of-tree clamping, OK.");
        }
        finally
        {
            camera.Free();
        }
    }
}
