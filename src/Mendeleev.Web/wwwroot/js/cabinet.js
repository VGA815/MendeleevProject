// The cabinet's only script, served from this site (FR-WEB-10: no third-party scripts).
// Everything works without it; the script adds copying and the payment status polling (ТЗ 27, «Кабинет»).
(function () {
    'use strict';

    function copy(button) {
        var value = button.getAttribute('data-copy');
        var label = button.textContent;
        var done = function () {
            button.textContent = 'Скопировано';
            setTimeout(function () { button.textContent = label; }, 2000);
        };
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(value).then(done, function () { });
            return;
        }
        var area = document.createElement('textarea');
        area.value = value;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        try { if (document.execCommand('copy')) { done(); } } catch (e) { }
        document.body.removeChild(area);
    }

    document.addEventListener('click', function (event) {
        var button = event.target.closest('[data-copy]');
        if (button) {
            event.preventDefault();
            copy(button);
        }
    });

    // Return from the aggregator: ask every 3 seconds, up to 60 seconds.
    var status = document.getElementById('payment-status');
    if (!status || status.getAttribute('data-state') !== 'pending') {
        return;
    }

    var paymentId = status.getAttribute('data-payment-id');
    var recheck = status.querySelector('[data-when-final-hide]');
    var startedAt = Date.now();
    var intervalMs = 3000;
    var limitMs = 60000;

    function show(state, accessPending) {
        status.querySelectorAll('[data-when]').forEach(function (block) {
            block.hidden = block.getAttribute('data-when') !== state;
        });
        var pending = status.querySelector('[data-when-access-pending]');
        if (pending) {
            pending.hidden = !accessPending;
        }
        if (recheck) {
            recheck.hidden = state === 'pending' || state === 'succeeded' || state === 'canceled';
        }
    }

    function poll() {
        fetch('/pay/return?handler=status&paymentId=' + encodeURIComponent(paymentId), {
            headers: { 'Accept': 'application/json' },
            credentials: 'same-origin',
            cache: 'no-store'
        })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (result) {
                if (result && result.state !== 'pending') {
                    show(result.state, result.accessPending);
                    return;
                }
                next();
            }, next);
    }

    function next() {
        if (Date.now() - startedAt + intervalMs > limitMs) {
            show('timeout', false);
            return;
        }
        setTimeout(poll, intervalMs);
    }

    show('pending', false);
    setTimeout(poll, intervalMs);
})();
