# Exam Forge — UI Kit

Версия 1.0 · 10.10.2026 · Статус: дизайн-спецификация для согласования.
Источник токенов: design/examforge-tokens.css. Самодостаточный пример: design/UI_KIT_PREVIEW.html. Это дизайн-предложение, не готовый интерфейс с сервером.

## 1. Характер

Современная академическая бумажная эстетика: тёплая бумага, чернильный синий, деликатный бордовый акцент, качественная типографика, тонкие правила и спокойные карточки. Сайт универсальный, не оформлять весь продукт как медицинскую клинику.

Избегать: тяжёлой текстуры пергамента, клипарта, декоративного латинского текста без смысла, бликов, glassmorphism, neon, крупных градиентов и бесконечных анимаций. Студенту во время контрольной — чистый экзаменационный лист.

## 2. Семантические цвета

| Token | Светлая | Тёмная |
|---|---|---|
| `--ef-bg` | `#f5f1e8` | `#171c20` |
| `--ef-surface` | `#fffdf7` | `#20272c` |
| `--ef-paper` | `#ffffff` | `#272f35` |
| `--ef-ink` | `#243142` | `#f0ece2` |
| `--ef-muted` | `#60665f` | `#c0c4bd` |
| `--ef-accent` | `#284d61` | `#a7c7d4` |
| `--ef-accent-hover` | `#1d3e50` | `#c1dde7` |
| `--ef-soft` | `#e8eef0` | `#2e3c44` |
| `--ef-burgundy` | `#7a4143` | `#e2a7a8` |
| `--ef-border` | `#d5d2c8` | `#45535b` |
| `--ef-success` | `#235d46` | `#a4d7b8` |
| `--ef-success-bg` | `#e6f2e8` | `#263e33` |
| `--ef-warning` | `#845718` | `#efd29b` |
| `--ef-warning-bg` | `#fff1d9` | `#443823` |
| `--ef-danger` | `#973c3c` | `#efb4ae` |
| `--ef-danger-bg` | `#f9e8e5` | `#452c2b` |
| `--ef-focus` | `#416f88` | `#b8d9e7` |

ink — основной текст, muted — вторичный, accent — главные действия/ссылки, burgundy — редкий редакционный акцент, soft — выделение/навигация, border — декоративные разделители. Для интерактивной границы input применять muted/focus, не считать слабую border достаточным контрастом индикатора управления. Success/warning/danger использовать с текстом и иконкой, не только заливкой. Primary label в светлой теме #ffffff, в тёмной #171c20.

Декоративный цвет не становится автоматически разрешённым текстовым. Contrast измерять для реальной пары, включая opacity и hover. Цель WCAG 2.2 AA: normal text 4.5:1, large text 3:1, значимые границы/индикаторы 3:1. Итоговое соответствие зависит от всего UI и требует проверки.

## 3. Шрифты

Для первого прототипа без сетевых зависимостей:
- Body/UI: system-ui, -apple-system, Segoe UI, Arial, sans-serif.
- Headings/термины на карточках: Georgia, Times New Roman, serif.
- Коды/технические идентификаторы: ui-monospace, Consolas, monospace.

Не скачивать шрифт при открытии страницы. Различия между ОС допустимы в концепте. Если нужен одинаковый фирменный вид — отдельное решение о локальных WOFF2 с кириллицей и проверкой лицензии; не заявлять такие файлы приложенными сейчас.

| Стиль | Desktop | Mobile | Line-height | Weight |
|---|---|---|---|---|
| Editorial hero | 44px | 32px | 1.1 | 400 serif |
| Page title | 32px | 28px | 1.2 | 400 serif |
| Section title | 24px | 22px | 1.3 | 400 serif |
| Card title | 20px | 20px | 1.35 | 400 serif |
| Body/question | 16px | 16px | 1.6 | 400 sans |
| Controls | 14–16px | 16px | 1.4 | 500–600 sans |
| Metadata | 13px | 13px | 1.5 | 400 sans |
| Eyebrow | 11px | 11px | 1.5 | 600 sans |
| Latin focus | 24px | 22px | 1.4 | 400 serif |

Не делать основной текст 11–12px. Заголовки не все caps; eyebrow можно uppercase с 0.12em. Долготы и диакритика не обрезаются line-height. Номер задания и таймер читаются без декоративной типографики.

## 4. Spacing, размеры, ритм

Базовая сетка 4px: 4/8/12/16/24/32/48. Карточка padding 24px desktop, 16px mobile. Между разделами 32–48px; form fields gap 16–24px. Control min-height 44px, target желательно 44×44px; текста на кнопку достаточно. Radius 6/10/16px; не использовать pill для каждого элемента. Shadow один мягкий, тёмная тема без теней.

Контент teacher max-width 1280px; student question max-width 820px; lecture reading 68–76ch. Nav rail 224px desktop, header 64px. Контент не растягивать на весь 4K экран. Tables data-dense, но row min-height 48px. Mobile padding 16px.

Breakpoints: <640 compact; 640–1023 tablet; >=1024 desktop. Учительский editor 3 columns только при достаточной ширине (около 1280+); на tablet свойства drawer; на телефоне последовательные panels. Student shell без teacher rail.

## 5. Основные компоненты

### 5.1 EfButton

Primary ink-blue, Secondary surface+контрастная border, Quiet text, Danger явно подписанный. Default/hover/active/focus-visible/disabled/loading. Loading сохраняет размер и текст контекста. Disabled причина рядом; не заменять ошибку просто серой кнопкой. Icon button с accessible name.

### 5.2 EfField

Label всегда виден; placeholder не label. Input, select, textarea: 44px+, border 1px muted для значимой границы, background paper. Focus outline 2–3px focus, offset 2px; error border danger и текст. Required объясняется словами/маркером с легендой. Код доступа autocomplete one-time-code где применимо, без реальных секретов в demo.

### 5.3 EfPaperCard / LectureCard / TestCard

Surface, border, radius-lg, shadow-paper. Небольшая категория, serif title, metadata, status и один главный CTA. Cover необязательная; никаких случайных внешних фото. Клик по всей карточке не конфликтует с вложенными кнопками.

### 5.4 EfBadge / EfCallout

Text + icon + semantic color. Badge font 13px, padding 4×8. Callout title/body/action, спокойная заливка. Saved не показывать до ACK; pending отделить от incorrect. Примеры label: «Сохранено», «Есть несохранённые изменения», «Нет связи», «Время истекло», «На проверке».

### 5.5 EfChoiceOption

Настоящий radio/checkbox, variant letter вторичен, текст 16px. Click area вся label. Selected outline accent+icon; фокус виден отдельно. Assessment не показывает correct/wrong colors. Practice feedback — только после разрешённого события. Для изображения масштабирование и alt с согласованным смыслом.

### 5.6 EfLetterTiles

Плитки минимум 44×44px, отдельный stable ID для одинаковых букв. Тап по source tile вставляет в next slot, тап по slot возвращает. Keyboard Enter/Space, focus, clear. Drag дополнительный, не единственный метод. Assessment не выдаёт подсказку длины сверх того, что задано автором.

### 5.7 EfMatching / EfOrdering / EfGapFill

Matching: click left then right; выбранная пара с текстовой маркировкой, отмена доступна. Не полагаться только на линии/цвет. Ordering: move up/down, optional drag; список с номерами. GapFill: focusable inline input минимум удобной ширины, labels по номеру; на телефоне лучше список полей рядом с текстом. Все элементы сериализуются по ID, не позиции UI.

### 5.8 EfQuestionHeader / Timer / Progress

Номер, баллы, оставшееся время, saved state. Не использовать красный на весь экран; последние секунды warning и текст. aria-live только на важных событиях, не каждую секунду. Progress не навигация при запрете возвратов. Таймер demo не считается серверной реализацией.

### 5.9 EfRubricPanel

criterion label, max, earned, reason/status. Не оценено отдельное состояние; итог предварительный. Manual override видим и требует комментария. Teacher-only key область никогда не рендерится student Assessment компонентом с CSS hiding.

### 5.10 EfGradebook

Чёткая table с caption, headers, статусами, сортировкой и фильтрами. Все попытки + best finalized. На телефоне карточки; horizontal scroll для доп. данных как запасной вариант. Процент округлён для итоговой grade, raw доступен преподавателю; 85.7→86→5.

### 5.11 EfMaterialViewer

Панель заголовка, тема, страницы/версия, разрешённый Download. PDF iframe/embed доступен с fallback «Открыть PDF» по защищённому endpoint. Не выдавать студенту тестовый PDF даже inline. Lecture reading отдельно от exam shell.

### 5.12 Dialog / Toast / Empty / Skeleton

Dialog: focus trap, Esc если безопасно, возвращение фокуса. Toast только вторичная информация, не единственный канал важной ошибки. Empty содержит объяснение и CTA. Skeleton не отражает выдуманные цифры. Не использовать blocking modal на каждый autosave.

## 6. Компоновки

Teacher: rail, bread crumbs, page heading, действия, рабочая область. Student: компактный header предмет/тест, question canvas, progress, таймер, footer actions. Lecture: читательская ширина, содержание/материалы. Assessment editor: структура слева, вопрос в центре, свойства справа; mobile preview доступен.

Главная портала — краткое объяснение, вход по коду, вход преподавателя; не публичная витрина студенческих результатов. Overview показывает актуальные действия, не «маркетинговые KPI».

## 7. Motion

Fast 140ms, normal 220ms, opacity/transform несколько px. Не анимировать layout при печати/ответе; не делать parallax в тесте. Respect prefers-reduced-motion: отключить transitions/animations и smooth scroll. Сохранение не должно зависеть от animationend.

## 8. Печать

A4, margin 18mm как старт, paper white/black ink независимо от темы. Student без nav/buttons/keys; название, code/version/variant, поля ФИО/группа/дата, задания и answer lines. Font 11–12pt, заголовки 16–18pt. break-inside: avoid у вопроса по возможности; большие вопросы могут занимать страницы. TeacherKey отдельный документ с тем же variant ID. Проверить реальные PDF, не только screenshot экрана.

В UI Kit preview печать — только визуальная демонстрационная карточка, не generator всей контрольной.

## 9. Доступность и правила текста

Labels, landmarks, semantic headings, keyboard focus, skip link. Одна H1 на активном экране. Нельзя color-only outcomes. 200% zoom, 360px, reduced motion. Крупные serif буквы не заменяют доступность. Частые ошибки объяснять действием: «Не удалось сохранить. Время продолжает идти», не «Ошибка 500». Озвучивание audio не autoplay.

Точность терминологии обеспечивается содержанием/проверкой преподавателя, не UI Kit. Медицинские примеры в demo — только визуальная иллюстрация, не опубликованный учебный банк.

## 10. Соответствие реализации

CSS prefix ef-, class names semantically clear, Razor components Ef*. CSS variables единый источник; no random hex/inline tokens. Theme local preference содержит только выбор темы, не student credentials. Системные шрифты и embedded SVG доступны без интернета. Не подключать CDN, внешние аналитики и шрифтовые сервисы.

До merge: screenshots light/dark 360/768/1440, choice keyboard, letter duplicate, offline/pending/error, A4 print; contrast проверен на реальных компонентах. Preview проверяется в браузере при реализации, текущий пакет имеет статические проверки файлов и токенов, не доказательство полной WCAG-сертификации.

## 11. Что показывает страница-пример

- Тёплая бумажная palette и typography.
- Карточки лекции и контрольной, buttons/fields/status.
- Переключение светлой/тёмной темы.
- Отдельный чистый student question.
- Выбор ответа, сборка слова кликами, режим Practice/Assessment.
- Калькулятор округления и оценки, в том числе 85.7→86→5.
- Печатный вид демонстрационного вопроса.

Нет backend, сохранения результатов, таймера сервера, auth, PDF import или реальных данных. Demo assessment не раскрывает key; practice может раскрыть fixture по кнопке. Это выставка компонентов, не рабочая контрольная.
