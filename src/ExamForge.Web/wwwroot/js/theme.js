// Тема: localStorage хранит только выбор темы (UI_KIT §10), без credentials.
(function () {
    'use strict';
    var KEY = 'ef-theme';

    function apply(preference) {
        var dark = preference === 'dark' ||
            (preference !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
        document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    }

    apply(localStorage.getItem(KEY));

    // Переключение — делегированный обработчик: кнопки заменяются Blazor
    // (enhanced navigation, интерактивный ререндер), прямые слушатели теряются.
    document.addEventListener('click', function (event) {
        var toggle = event.target && event.target.closest
            ? event.target.closest('[data-ef-theme-toggle]')
            : null;
        if (!toggle) return;
        var current = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
        var next = current === 'dark' ? 'light' : 'dark';
        localStorage.setItem(KEY, next);
        apply(next);
    });

    // Blazor enhanced navigation перезаписывает атрибуты <html> из ответа сервера,
    // где data-theme нет — возвращаем сохранённую тему после каждого перехода.
    document.addEventListener('enhanced-load', function () {
        apply(localStorage.getItem(KEY));
    });

    // Системная тема меняется только пока пользователь не сделал явный выбор.
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (!localStorage.getItem(KEY)) apply(null);
    });
})();
