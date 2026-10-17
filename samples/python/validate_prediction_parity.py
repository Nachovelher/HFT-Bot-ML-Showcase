"""
=============================================================
validate_prediction_parity.py — paridad de PREDICCIONES
=============================================================
Compara la probabilidad final que produce el evaluador C# real
del bot (XGBoostEvaluator + IsotonicCalibrator, via
tools/parity_harness) contra la referencia Python (xgboost +
calibración isotónica del sidecar _cal.json).

A diferencia de validate_feature_parity.py (que valida el
VECTOR de features), esto valida la CADENA DE PROBABILIDAD:
  - recorrido de árboles (default_left para NaN, float32)
  - base_score / margen inicial
  - sigmoid + calibración isotónica + clip [0.001, 0.999]

Habría detectado los 3 bugs históricos del evaluador:
  1. base_score ignorado (probs infladas +0.53 en logit)
  2. NaN siempre a la izquierda (ignoraba default_left)
  3. calibración _cal.json nunca aplicada en el Ensemble engine

Uso:
  python tools/validate_prediction_parity.py --models_dir output/models_3m
  python tools/validate_prediction_parity.py --models_dir <dir> --tol 1e-4 --n 400

Exit codes: 0 = paridad OK, 1 = divergencia > tol, 2 = error de entorno.
=============================================================
"""

import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np

# Consolas Windows con cp1252: no petar por caracteres no mapeables
try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except AttributeError:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_DIR = os.path.join(HERE, "parity_harness")

# Roslyn csc del NT8 dev box (fallback si no hay dotnet SDK)
CSC_CANDIDATES = [
    os.environ.get("PARITY_CSC", ""),
    r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
]


# ─────────────────────────────────────────────────────────────
# 1. BUILD DEL HARNESS C#
# ─────────────────────────────────────────────────────────────

def build_harness(verbose=True):
    """
    Compila el harness. Devuelve la lista de argumentos base para
    invocarlo (p.ej. ["dotnet", "run..."] o ["ParityHarness.exe"]).
    Prioridad: dotnet SDK → Roslyn csc (framework .exe).
    """
    # ── Opción A: dotnet SDK ──
    dotnet = shutil.which("dotnet")
    if dotnet:
        r = subprocess.run(
            [dotnet, "build", "-c", "Release", "--nologo", "-v", "q"],
            cwd=HARNESS_DIR, capture_output=True, text=True)
        if r.returncode == 0:
            dlls = glob.glob(os.path.join(
                HARNESS_DIR, "bin", "Release", "net*", "ParityHarness.dll"))
            if dlls:
                if verbose:
                    print(f"[build] dotnet OK → {dlls[0]}")
                return [dotnet, dlls[0]]
        elif verbose:
            print(f"[build] dotnet build falló, probando csc…\n{r.stdout[-800:]}")

    # ── Opción B: Roslyn csc → .NET Framework exe ──
    csc = next((c for c in CSC_CANDIDATES if c and os.path.exists(c)), None)
    if csc:
        outdir = tempfile.mkdtemp(prefix="parity_harness_")
        exe = os.path.join(outdir, "ParityHarness.exe")
        sources = [
            os.path.join(HARNESS_DIR, "Program.cs"),
            os.path.join(HERE, "..", "MiniJson.cs"),
            os.path.join(HERE, "..", "HFT_BOT_ML_Common.cs"),
            # Sin dependencias NT8 — compile-check gratis (igual que el csproj)
            os.path.join(HERE, "..", "OrderFlowProxyExtractor.cs"),
            os.path.join(HERE, "..", "FullFeatureExtractor.cs"),
        ]
        r = subprocess.run(
            [csc, "-langversion:7.3", "-optimize+", f"-out:{exe}"] + sources,
            capture_output=True, text=True)
        if r.returncode == 0 and os.path.exists(exe):
            if verbose:
                print(f"[build] csc OK → {exe}")
            return [exe]
        print(f"[build] csc falló:\n{r.stdout[-1500:]}")

    print("[ERROR] No se pudo compilar el harness (ni dotnet ni csc disponibles).")
    return None


# ─────────────────────────────────────────────────────────────
# 2. REFERENCIA PYTHON
# ─────────────────────────────────────────────────────────────

def model_num_features(model_path):
    with open(model_path) as f:
        root = json.load(f)
    lmp = root["learner"]["learner_model_param"]
    return int(lmp["num_feature"])


def python_predict(model_path, X, apply_cal=True):
    """
    Referencia: xgboost Booster.predict (probabilidad para binary:logistic)
    + calibración isotónica del sidecar _cal.json replicando el
    CalibratedModel del pipeline (interp lineal + clip [0.001, 0.999]).
    """
    import xgboost as xgb
    bst = xgb.Booster()
    bst.load_model(model_path)
    dm = xgb.DMatrix(np.asarray(X, dtype=np.float32), missing=np.nan,
                     feature_names=bst.feature_names)
    raw = bst.predict(dm).astype(np.float64)

    cal_path = model_path[:-len(".json")] + "_cal.json"
    calibrated = raw
    has_cal = False
    if apply_cal and os.path.exists(cal_path):
        with open(cal_path) as f:
            cal_data = json.load(f)
        cals = cal_data.get("calibrators", [])
        if cals and len(cals[0].get("X_thresholds", [])) >= 2:
            xt = np.asarray(cals[0]["X_thresholds"], dtype=np.float64)
            yt = np.asarray(cals[0]["y_thresholds"], dtype=np.float64)
            # sklearn IsotonicRegression(out_of_bounds='clip').predict ==
            # interpolación lineal con clamp en extremos == np.interp
            calibrated = np.clip(np.interp(raw, xt, yt), 0.001, 0.999)
            has_cal = True

    return calibrated, raw, has_cal


# ─────────────────────────────────────────────────────────────
# 3. MATRIZ DE PRUEBA
# ─────────────────────────────────────────────────────────────

def build_test_matrix(n_features, n_rows, seed=42):
    """
    Vectores sintéticos que ejercitan los caminos críticos:
      - valores normales (escala mixta)
      - NaN dispersos (~20%) → ejercita default_left por nodo
      - filas completamente NaN
      - extremos ±1e5 (thresholds float32)
      - ceros exactos
    """
    rng = np.random.default_rng(seed)
    X = rng.normal(0.0, 1.0, size=(n_rows, n_features)) * \
        rng.choice([0.1, 1.0, 5.0, 50.0], size=(n_rows, 1))

    nan_mask = rng.random(X.shape) < 0.20
    X[nan_mask] = np.nan

    n_special = min(20, n_rows // 10)
    X[:n_special // 2] = np.nan                       # filas todo-NaN
    X[n_special // 2:n_special] = 0.0                 # filas todo-cero
    if n_rows > n_special + 4:
        X[n_special]     = 1e5
        X[n_special + 1] = -1e5

    return X


def write_csv(X, path):
    with open(path, "w") as f:
        for row in X:
            f.write(",".join(
                "nan" if np.isnan(v) else repr(float(v)) for v in row))
            f.write("\n")


# ─────────────────────────────────────────────────────────────
# 4. COMPARACIÓN POR MODELO
# ─────────────────────────────────────────────────────────────

def check_model(model_path, harness_cmd, n_rows, tol, workdir):
    name = os.path.basename(model_path)
    try:
        n_feat = model_num_features(model_path)
    except Exception as e:
        return {"model": name, "status": "SKIP", "detail": f"sin num_feature ({e})"}

    X = build_test_matrix(n_feat, n_rows)
    csv_path = os.path.join(workdir, "features.csv")
    out_path = os.path.join(workdir, "probs_cs.csv")
    write_csv(X, csv_path)

    r = subprocess.run(harness_cmd + [model_path, csv_path, out_path],
                       capture_output=True, text=True)
    if r.returncode != 0:
        return {"model": name, "status": "ERROR",
                "detail": f"harness rc={r.returncode}: {r.stderr[-400:]}"}

    cs = np.loadtxt(out_path, delimiter=",", ndmin=2)
    cs_prob, cs_raw = cs[:, 0], cs[:, 1]

    py_prob, py_raw, has_cal = python_predict(model_path, X)

    diff_raw  = np.abs(cs_raw - py_raw)
    diff_prob = np.abs(cs_prob - py_prob)

    status = "OK" if diff_prob.max() <= tol and diff_raw.max() <= tol else "FAIL"
    return {
        "model": name, "status": status, "n_features": n_feat,
        "cal": has_cal,
        "max_diff_raw":  float(diff_raw.max()),
        "max_diff_prob": float(diff_prob.max()),
        "mean_diff_prob": float(diff_prob.mean()),
        "n_over_tol": int((diff_prob > tol).sum()),
    }


# ─────────────────────────────────────────────────────────────
# 5. MAIN
# ─────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(
        description="Paridad de predicciones Python<->C# (evaluador XGBoost del bot)")
    ap.add_argument("--models_dir", default="output/models_3m")
    ap.add_argument("--patterns", nargs="+",
                    default=["meta_long*.json", "meta_short*.json",
                             "xgb_Trending_*.json", "xgb_Volatile_*.json"])
    ap.add_argument("--n", type=int, default=400,
                    help="filas de prueba por modelo (default 400)")
    ap.add_argument("--tol", type=float, default=1e-4,
                    help="tolerancia sobre |prob_cs - prob_py| (default 1e-4)")
    ap.add_argument("--max_models", type=int, default=0,
                    help="limitar nº de modelos (0 = todos)")
    args = ap.parse_args()

    print("=" * 64)
    print("  PARIDAD DE PREDICCIONES Python <-> C#")
    print("=" * 64)

    if not os.path.isdir(args.models_dir):
        print(f"[ERROR] models_dir no existe: {args.models_dir}")
        return 2

    models = []
    for pat in args.patterns:
        models.extend(glob.glob(os.path.join(args.models_dir, pat)))
    models = sorted(set(m for m in models if not m.endswith("_cal.json")))
    if args.max_models > 0:
        models = models[:args.max_models]
    if not models:
        print(f"[ERROR] Ningún modelo en {args.models_dir} con {args.patterns}")
        return 2
    print(f"Modelos a validar: {len(models)}  |  filas/modelo: {args.n}  |  tol: {args.tol}")

    harness_cmd = build_harness()
    if harness_cmd is None:
        return 2

    workdir = tempfile.mkdtemp(prefix="parity_")
    results = []
    for m in models:
        res = check_model(m, harness_cmd, args.n, args.tol, workdir)
        results.append(res)
        if res["status"] in ("OK", "FAIL"):
            print(f"  [{res['status']:>4}] {res['model']:<44} "
                  f"cal={'sí' if res['cal'] else 'no '} "
                  f"max|Δprob|={res['max_diff_prob']:.2e} "
                  f"max|Δmargin|={res['max_diff_raw']:.2e} "
                  f"(>{args.tol:g}: {res['n_over_tol']})")
        else:
            print(f"  [{res['status']:>4}] {res['model']:<44} {res.get('detail','')}")

    n_fail = sum(1 for r in results if r["status"] in ("FAIL", "ERROR"))
    n_ok   = sum(1 for r in results if r["status"] == "OK")
    print("-" * 64)
    print(f"  OK: {n_ok}   FAIL/ERROR: {n_fail}   SKIP: {len(results)-n_ok-n_fail}")
    if n_fail:
        print("  X PARIDAD ROTA — NO desplegar. El C# no reproduce las "
              "probabilidades de Python.")
        return 1
    print("  OK Paridad de predicciones OK.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
