// Boot Exam Forge: индикатор подключения. Внешний файл (не inline): CSP
// script-src 'self' запрещает inline-скрипты — inline-вариант молча не выполнялся.
//
// Здесь ТОЛЬКО индикация: Blazor стартует стандартным автостартом самого
// blazor.web.js. Мы не перехватываем Blazor.start (перехват гонялся с автостартом
// и мог навсегда оставить блокировку форм) и не блокируем submit: с автостартом
// подключение быстрое, а вечная блокировка форм при сбое индикатора была хуже.
(function () {
    function ready() {
        var el = document.getElementById('ef-boot');
        if (el) el.remove();
    }
    function failed() {
        var el = document.getElementById('ef-boot');
        if (!el) return;
        el.classList.add('ef-boot--failed');
        var text = el.querySelector('.ef-boot__text');
        if (text) text.textContent = 'Сайт не ответил. Проверьте интернет, антивирус/расширения и обновите страницу.';
    }

    var waited = 0;
    var poll = setInterval(function () {
        waited += 300;
        if (window.Blazor) {
            clearInterval(poll);
            // небольшой гросс-период: скрипт Blazor загружен, соединение поднимается
            setTimeout(ready, 400);
        } else if (waited > 15000) {
            clearInterval(poll);
            failed();
        }
    }, 300);
})();
