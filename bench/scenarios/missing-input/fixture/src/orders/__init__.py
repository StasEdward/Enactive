"""Orders: priced from the catalog, invoiced through billing."""

from billing import invoice_total
from catalog import price_of


def order_total(items):
    return invoice_total([{"amount": price_of(item)} for item in items])
