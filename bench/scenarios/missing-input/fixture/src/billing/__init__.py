"""Invoices and payments."""


def invoice_total(lines):
    return sum(line["amount"] for line in lines)
