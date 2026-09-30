# Tariffs

Delivery prices per zone and weight band.

The published rate table, `data/rates.csv`, is generated and not kept in git. Before running the tests:

    python tools/make_rates.py
    dotnet test Tariffs.sln
