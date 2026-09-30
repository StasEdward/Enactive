"""Writes the published rate table, data/rates.csv, from the tariff rules below.

Run from the project root:  python tools/make_rates.py
"""

import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Base price per zone and the price per kilogram past the first; bands close at these weights.
ZONES = {"A": (4.20, 0.95), "B": (5.10, 1.05), "C": (7.40, 1.60)}
BANDS = [1, 2, 5, 10, 20]


def main():
    rows = ["zone,maxKg,price"]
    for zone, (base, per_kg) in ZONES.items():
        for max_kg in BANDS:
            rows.append(f"{zone},{max_kg},{base + per_kg * (max_kg - 1):.2f}")
    out = ROOT / "data" / "rates.csv"
    out.parent.mkdir(exist_ok=True)
    out.write_text("\n".join(rows) + "\n", encoding="utf-8")
    print(f"{len(rows) - 1} rates written to {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
