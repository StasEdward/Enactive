"""Messages sent when an order is placed."""

import orders


def confirmation(items):
    return f"Your order comes to {orders.order_total(items)}."
