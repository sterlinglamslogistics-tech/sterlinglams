// ─── Mobile Menu ──────────────────────────────────────────────────────────
document.getElementById('mobile-menu-toggle')?.addEventListener('click', () => {
    const menu = document.getElementById('mobile-menu');
    menu?.classList.toggle('hidden');
});

// ─── Cart Badge Update ────────────────────────────────────────────────────
function updateCartBadge(count) {
    const badge = document.getElementById('cart-badge');
    if (!badge) return;
    badge.textContent = count;
    badge.classList.toggle('hidden', count === 0);
}

// ─── Cart line edit (Cart page) ───────────────────────────────────────────
function postCart(url, params) {
    return fetch(url, { method: 'POST', body: new URLSearchParams(params) }).then(r => r.json());
}

// Quantity +/-
document.querySelectorAll('.qty-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
        const display = btn.parentElement.querySelector('.qty-display');
        const current = parseInt(display?.textContent.trim(), 10) || 0;
        const next = current + parseInt(btn.dataset.delta, 10);
        const params = { productId: btn.dataset.productId, quantity: next };
        if (btn.dataset.variantId) params.variantId = btn.dataset.variantId;
        try {
            const data = await postCart('/Cart/UpdateQuantity', params);
            if (data.success) location.reload();   // re-render line totals + subtotal accurately
        } catch (err) { console.error('Cart update failed', err); }
    });
});

// Remove item
document.querySelectorAll('.remove-item').forEach(btn => {
    btn.addEventListener('click', async () => {
        const params = { productId: btn.dataset.productId };
        if (btn.dataset.variantId) params.variantId = btn.dataset.variantId;
        try {
            const data = await postCart('/Cart/Remove', params);
            if (data.success) location.reload();
        } catch (err) { console.error('Cart remove failed', err); }
    });
});

// ─── Wishlist Toggle (list page) ──────────────────────────────────────────
document.querySelectorAll('.wishlist-toggle').forEach(btn => {
    btn.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const productId = btn.dataset.productId;
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';

        try {
            const res = await fetch('/Wishlist/Toggle', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: `productId=${productId}&__RequestVerificationToken=${encodeURIComponent(token)}`
            });
            const data = await res.json();
            if (data.success) {
                const svg = btn.querySelector('svg');
                if (svg) {
                    svg.setAttribute('fill', data.added ? 'currentColor' : 'none');
                }
            }
        } catch (err) {
            console.error('Wishlist toggle failed', err);
        }
    });
});
