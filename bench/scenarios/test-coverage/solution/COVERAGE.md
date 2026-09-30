# Test coverage of Shelf

Before: 2 tests, both on `Add`/`CountOf`. `Remove`, `Total`, `Kinds` and the refused counts were untested.

Added 4 tests: removing (including the last one of a kind), removing more than there is, `Total` over several kinds,
`CountOf` of a missing item and a refused count. All 6 pass (`dotnet test Shelf.sln`).
