#!/usr/bin/env python3
"""LabControl action icons — one source of truth.

Every icon is a single path-geometry string on a 24x24 grid, drawn as a 2 px round-capped,
round-joined stroke with no fill (the Lucide convention). Only path data is used, so the same
string goes into an SVG <path d>, an Avalonia StreamGeometry and an SKPath (student overlay).

Run:  python3 build/icons.py      (writes ../icons/*.svg, ../icons/index.html, build/icons.json)
"""
import json, math, os, html

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'icons')


def f(v):
    s = ('%.2f' % v).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def rect(x, y, w, h, r=0):
    if r <= 0:
        return f'M{f(x)} {f(y)}h{f(w)}v{f(h)}h{f(-w)}z'
    return (f'M{f(x + r)} {f(y)}h{f(w - 2 * r)}a{f(r)} {f(r)} 0 0 1 {f(r)} {f(r)}v{f(h - 2 * r)}'
            f'a{f(r)} {f(r)} 0 0 1 {f(-r)} {f(r)}h{f(-(w - 2 * r))}a{f(r)} {f(r)} 0 0 1 {f(-r)} {f(-r)}'
            f'v{f(-(h - 2 * r))}a{f(r)} {f(r)} 0 0 1 {f(r)} {f(-r)}z')


def circle(cx, cy, r):
    return f'M{f(cx - r)} {f(cy)}a{f(r)} {f(r)} 0 1 0 {f(2 * r)} 0a{f(r)} {f(r)} 0 1 0 {f(-2 * r)} 0z'


def dot(cx, cy):
    # A filled-looking dot: a tiny circle the 2 px stroke closes.
    return circle(cx, cy, 0.5)


def line(x1, y1, x2, y2):
    return f'M{f(x1)} {f(y1)}L{f(x2)} {f(y2)}'


def poly(*pts, close=False):
    s = 'M' + ' L'.join(f'{f(x)} {f(y)}' for x, y in zip(pts[0::2], pts[1::2]))
    return s + ('z' if close else '')


def gear():
    pts = []
    teeth = 8
    for i in range(teeth * 4):
        a = (i / (teeth * 4)) * 2 * math.pi - math.pi / 2
        r = 9.5 if (i % 4) in (1, 2) else 7.4
        pts += [12 + r * math.cos(a), 12 + r * math.sin(a)]
    return poly(*pts, close=True) + circle(12, 12, 3)


MONITOR = rect(2, 3, 20, 14, 2) + line(8, 21, 16, 21) + line(12, 17, 12, 21)
FILE = 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z' + poly(14, 2, 14, 8, 20, 8)
LAPTOP = ('M20 16V7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v9M2.5 19.5 4 16h16l1.5 3.5a1 1 0 0 1-.9 1.5H3.4'
          'a1 1 0 0 1-.9-1.5z')
GLOBE = circle(12, 12, 10) + line(2, 12, 22, 12) + \
    'M12 2a15 15 0 0 1 4 10 15 15 0 0 1-4 10 15 15 0 0 1-4-10 15 15 0 0 1 4-10z'
KEY = circle(7.5, 15.5, 5.5) + line(11.4, 11.6, 21, 2) + poly(15.5, 7.5, 18.5, 10.5, 22, 7, 19, 4)
USER = circle(12, 7, 4) + 'M5 21a7 7 0 0 1 14 0'
LOCK_BODY = rect(4, 11, 16, 10, 2)

# name: (geometry, Ukrainian meaning, English meaning, group)
ICONS = {
    # ---- navigation
    'class':        (rect(3, 3, 7, 7, 1.5) + rect(14, 3, 7, 7, 1.5) + rect(3, 14, 7, 7, 1.5) + rect(14, 14, 7, 7, 1.5), 'Клас (мозаїка)', 'Class (mosaic)', 'nav'),
    'programs':     ('M21 8 12 3 3 8v8l9 5 9-5z' + poly(3, 8, 12, 13, 21, 8) + line(12, 13, 12, 21) + line(7.5, 5.5, 16.5, 10.5), 'Програми', 'Programs', 'nav'),
    'scripts':      (rect(2, 4, 20, 16, 2) + poly(6, 9, 10, 12, 6, 15) + line(12, 15, 17, 15), 'Скрипти', 'Scripts', 'nav'),
    'activity':     (poly(22, 12, 18, 12, 15, 21, 9, 3, 6, 12, 2, 12), 'Активність (завдання й події)', 'Activity (jobs and events)', 'nav'),
    'admin':        ('M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z' + poly(9, 12, 11, 14, 15, 10), 'Адміністрування лабораторії', 'Lab administration', 'nav'),
    'settings':     (gear(), 'Налаштування цього комп’ютера', 'Settings (this computer)', 'nav'),
    'labs':         ('M12 2 2 7l10 5 10-5z' + poly(2, 17, 12, 22, 22, 17) + poly(2, 12, 12, 17, 22, 12), 'Мої лабораторії', 'My labs', 'nav'),
    'leave':        ('M3 21h18' + 'M6 21V4a1 1 0 0 1 1-1h7v18' + 'M14 3l4 1.5V21' + dot(11, 12), 'Вийти з кімнати (від’єднатися)', 'Leave the room (disconnect)', 'nav'),
    'sidebar':      (rect(3, 3, 18, 18, 2) + line(9, 3, 9, 21), 'Згорнути бічну панель', 'Collapse sidebar', 'nav'),
    # ---- lesson
    'cast':         (MONITOR + line(12, 7, 12, 13) + poly(9, 10, 12, 7, 15, 10), 'Показати мій екран', 'Show my screen', 'lesson'),
    'cast-off':     (MONITOR + line(9, 7, 15, 13) + line(15, 7, 9, 13), 'Припинити показ', 'Stop showing', 'lesson'),
    'text-mode':    (poly(4, 7, 4, 4, 20, 4, 20, 7) + line(9, 20, 15, 20) + line(12, 4, 12, 20), 'Режим «Текст» (JPEG)', 'Text mode (JPEG)', 'lesson'),
    'video-mode':   (rect(2, 4, 20, 16, 2) + 'M10 9l5 3-5 3z', 'Режим «Відео» (H.264)', 'Video mode (H.264)', 'lesson'),
    'lock':         (LOCK_BODY + 'M8 11V7a4 4 0 0 1 8 0v4', 'Заблокувати екрани', 'Lock screens', 'lesson'),
    'unlock':       (LOCK_BODY + 'M8 11V7a4 4 0 0 1 7.9-1', 'Розблокувати', 'Unlock', 'lesson'),
    'send-files':   (FILE + line(12, 18, 12, 12) + poly(9, 15, 12, 12, 15, 15), 'Надіслати файли', 'Send files', 'lesson'),
    'collect':      ('M22 12h-6l-2 3h-4l-2-3H2' + 'M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z', 'Зібрати роботи', 'Collect work', 'lesson'),
    'view':         (poly(15, 3, 21, 3, 21, 9) + poly(9, 21, 3, 21, 3, 15) + line(21, 3, 14, 10) + line(3, 21, 10, 14), 'Відкрити на весь екран', 'Open full view', 'lesson'),
    'control':      ('M4 4l7 17 2.5-7.5L21 11z', 'Керувати (миша й клавіатура)', 'Control (mouse and keyboard)', 'lesson'),
    'keyboard':     (rect(2, 6, 20, 12, 2) + dot(6, 10) + dot(10, 10) + dot(14, 10) + dot(18, 10) + line(8, 14, 16, 14), 'Ctrl+Alt+Del', 'Ctrl+Alt+Del', 'lesson'),
    'win-key':      ('M3 5.5 10 4.5v7H3z' + 'M12 4.2 21 3v8.5h-9z' + 'M3 13h7v7l-7-1z' + 'M12 13h9v8l-9-1.3z', 'Клавіша Windows', 'Windows key', 'lesson'),
    'quality':      ('M12 14l4-4' + 'M3.3 19a10 10 0 1 1 17.4 0', 'Якість зображення', 'Picture quality', 'lesson'),
    # ---- exam & internet
    'exam':         (rect(8, 2, 8, 4, 1) + 'M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2' + poly(9, 14, 11, 16, 15, 12), 'Іспит', 'Exam', 'control'),
    'timer':        (circle(12, 13, 8) + line(10, 2, 14, 2) + line(12, 13, 15, 10), 'Таймер / залишок часу', 'Timer / time left', 'control'),
    'timer-plus':   (circle(12, 13, 8) + line(10, 2, 14, 2) + line(12, 10, 12, 16) + line(9, 13, 15, 13), 'Продовжити іспит', 'Extend exam', 'control'),
    'flag':         ('M4 22V4' + 'M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1', 'Завершити іспит', 'End exam', 'control'),
    'globe':        (GLOBE, 'Інтернет', 'Internet', 'control'),
    'globe-off':    (GLOBE + line(3, 3, 21, 21), 'Без інтернету', 'No internet', 'control'),
    'list-check':   (line(10, 6, 21, 6) + line(10, 12, 21, 12) + line(10, 18, 21, 18) + poly(3, 6, 4.5, 7.5, 7, 5) + poly(3, 12, 4.5, 13.5, 7, 11) + poly(3, 18, 4.5, 19.5, 7, 17), 'Лише дозволені (список)', 'Allowed list only', 'control'),
    'apps':         (rect(3, 3, 7, 7, 1) + rect(14, 3, 7, 7, 1) + rect(3, 14, 7, 7, 1) + line(17.5, 14, 17.5, 21) + line(14, 17.5, 21, 17.5), 'Дозволені програми', 'Allowed programs', 'control'),
    'message':      ('M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z', 'Повідомлення студентам', 'Message to students', 'control'),
    # ---- power
    'power':        ('M12 2v10' + 'M18.4 6.6a9 9 0 1 1-12.8 0', 'Вимкнути / вимкнено', 'Shut down / off', 'power'),
    'wake':         (line(12, 2, 12, 9) + poly(8, 6, 12, 2, 16, 6) + line(4.9, 10.9, 6.3, 12.3) + line(2, 18, 4, 18) + line(20, 18, 22, 18) + line(19.1, 10.9, 17.7, 12.3) + line(22, 22, 2, 22) + 'M16 18a4 4 0 0 0-8 0', 'Увімкнути (Wake-on-LAN)', 'Wake (Wake-on-LAN)', 'power'),
    'reboot':       ('M21 12a9 9 0 1 1-3-6.7L21 8' + poly(21, 3, 21, 8, 16, 8), 'Перезавантажити', 'Restart', 'power'),
    'logoff':       ('M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4' + poly(16, 17, 21, 12, 16, 7) + line(21, 12, 9, 12), 'Вийти із системи (сеанс студента)', 'Log off', 'power'),
    # ---- computers
    'monitor':      (MONITOR, 'Комп’ютер', 'Computer', 'pcs'),
    'monitor-plus': (MONITOR + line(12, 6.5, 12, 13.5) + line(8.5, 10, 15.5, 10), 'Додати комп’ютери', 'Add computers', 'pcs'),
    'usb':          (rect(9, 2, 6, 6, 1) + rect(6, 8, 12, 14, 2) + line(11, 4.5, 11, 5.5) + line(13, 4.5, 13, 5.5), 'USB-флешка (інсталятор)', 'USB stick (installer)', 'pcs'),
    'reset-user':   (circle(10, 7, 4) + 'M3 21v-1a7 7 0 0 1 9.5-6.5' + 'M15 18a3.5 3.5 0 1 0 1.1-2.6' + poly(15.6, 13, 15.6, 15.6, 18.2, 15.6), 'Скинути профіль', 'Reset profile', 'pcs'),
    'update':       (circle(12, 12, 10) + poly(16, 12, 12, 8, 8, 12) + line(12, 16, 12, 8), 'Оновити агент / застарілий агент', 'Update agent / agent outdated', 'pcs'),
    'trash':        ('M3 6h18' + 'M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6' + 'M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2' + line(10, 11, 10, 17) + line(14, 11, 14, 17), 'Видалити з лабораторії', 'Remove from lab', 'pcs'),
    'ban':          (circle(12, 12, 10) + line(4.9, 4.9, 19.1, 19.1), 'Відкликати / заборонити', 'Revoke / withdraw', 'pcs'),
    'grip':         (dot(9, 5) + dot(9, 12) + dot(9, 19) + dot(15, 5) + dot(15, 12) + dot(15, 19), 'Перетягнути (план кімнати)', 'Drag (room plan)', 'pcs'),
    'student':      (USER, 'Студент / користувач', 'Student / user', 'pcs'),
    'elsewhere':    ('M9 17H7A5 5 0 0 1 7 7h2' + 'M15 7h2a5 5 0 0 1 4 8' + line(8, 12, 12, 12) + line(2, 2, 22, 22), 'Утримує інша консоль', 'Held by another console', 'pcs'),
    'swap':         (poly(8, 3, 4, 7, 8, 11) + line(4, 7, 20, 7) + poly(16, 21, 20, 17, 16, 13) + line(20, 17, 4, 17), 'Перебрати лабораторію', 'Take over the lab', 'pcs'),
    'install':      (MONITOR + line(12, 6, 12, 13) + poly(9, 10, 12, 13, 15, 10), 'Встановити на ПК', 'Install on PCs', 'pcs'),
    # ---- grid view
    'fit':          ('M3 7V5a2 2 0 0 1 2-2h2' + 'M17 3h2a2 2 0 0 1 2 2v2' + 'M21 17v2a2 2 0 0 1-2 2h-2' + 'M7 21H5a2 2 0 0 1-2-2v-2' + rect(7, 7, 4, 4, .5) + rect(13, 7, 4, 4, .5) + rect(7, 13, 4, 4, .5) + rect(13, 13, 4, 4, .5), 'Вмістити все (автоматична сітка)', 'Fit all (auto grid)', 'grid'),
    'room':         (rect(3, 3, 18, 18, 2) + rect(6, 6, 4, 3, .5) + rect(14, 6, 4, 3, .5) + rect(6, 11, 4, 3, .5) + rect(14, 11, 4, 3, .5) + line(9, 17.5, 15, 17.5), 'План кімнати', 'Room plan', 'grid'),
    'select-all':   (rect(3, 3, 18, 18, 2) + poly(8, 12, 11, 15, 16, 9), 'Вибрати всі', 'Select all', 'grid'),
    'select-none':  (rect(3, 3, 18, 18, 2) + line(9, 9, 15, 15) + line(15, 9, 9, 15), 'Зняти вибір', 'Clear selection', 'grid'),
    'lasso':        ('M5 3a2 2 0 0 0-2 2' + 'M19 3a2 2 0 0 1 2 2' + 'M21 19a2 2 0 0 1-2 2' + 'M5 21a2 2 0 0 1-2-2' + line(9, 3, 10, 3) + line(9, 21, 10, 21) + line(14, 3, 15, 3) + line(14, 21, 15, 21) + line(3, 9, 3, 10) + line(21, 9, 21, 10) + line(3, 14, 3, 15) + line(21, 14, 21, 15), 'Виділення рамкою', 'Rubber-band selection', 'grid'),
    'zoom-in':      (circle(11, 11, 8) + line(21, 21, 16.7, 16.7) + line(11, 8, 11, 14) + line(8, 11, 14, 11), 'Більші плитки', 'Larger tiles', 'grid'),
    'zoom-out':     (circle(11, 11, 8) + line(21, 21, 16.7, 16.7) + line(8, 11, 14, 11), 'Менші плитки', 'Smaller tiles', 'grid'),
    'legend':       (circle(12, 12, 10) + 'M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3' + dot(12, 17), 'Що означають позначки (довідка)', 'What the marks mean (help)', 'grid'),
    # ---- key & lab admin
    'key':          (KEY, 'Ключ лабораторії', 'Lab key', 'admin'),
    'remember':     (LAPTOP + poly(9, 10.5, 11, 12.5, 15, 8.5), 'Запам’ятати ключ на цьому комп’ютері', 'Remember key on this computer', 'admin'),
    'forget':       (LAPTOP + line(9.5, 8, 14.5, 13) + line(14.5, 8, 9.5, 13), 'Забути ключ на цьому комп’ютері', 'Forget key on this computer', 'admin'),
    'lifebuoy':     (circle(12, 12, 10) + circle(12, 12, 4) + line(4.9, 4.9, 9.2, 9.2) + line(14.8, 14.8, 19.1, 19.1) + line(14.8, 9.2, 19.1, 4.9) + line(4.9, 19.1, 9.2, 14.8), 'Код відновлення', 'Recovery code', 'admin'),
    'key-move':     (circle(6.5, 17.5, 4) + line(9.4, 14.6, 15, 9) + line(12.5, 11.5, 14.5, 13.5) + poly(15, 3, 21, 3, 21, 9) + line(21, 3, 16, 8), 'Перенести лабораторію на новий ключ', 'Move lab to a new key', 'admin'),
    'holders':      (circle(9, 7, 4) + 'M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2' + 'M16 3.1a4 4 0 0 1 0 7.8' + 'M21 21v-2a4 4 0 0 0-3-3.9', 'Власники ключа', 'Key holders', 'admin'),
    'backup':       (rect(2, 3, 20, 5, 1) + 'M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8' + line(10, 12, 14, 12), 'Резервна копія', 'Backup', 'admin'),
    'laptop':       (LAPTOP, 'Пристрої викладачів', 'Teacher devices', 'admin'),
    'approve':      (circle(12, 12, 9.5) + poly(8.5, 12, 11, 14.5, 15.5, 9.5), 'Авторизувати / готово', 'Authorize / done', 'admin'),
    'lab-file':     (FILE + line(8, 15, 15, 15) + poly(12, 12, 15, 15, 12, 18), 'Файл лабораторії для викладачів', 'Lab file for teachers', 'admin'),
    'ticket':       ('M2 9a3 3 0 0 1 0 6v2a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-2a3 3 0 0 1 0-6V7a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2z' + line(13, 5, 13, 7) + line(13, 11, 13, 13) + line(13, 17, 13, 19), 'Коди реєстрації', 'Enrolment codes', 'admin'),
    'certificate':  (rect(3, 4, 18, 12, 2) + line(7, 8, 17, 8) + line(7, 12, 11, 12) + circle(16, 16, 3) + poly(14.5, 18.5, 14, 22, 16, 21, 18, 22, 17.5, 18.5), 'Сертифікат (поновлення)', 'Certificate (renewal)', 'admin'),
    'network':      (rect(16, 16, 6, 6, 1) + rect(2, 16, 6, 6, 1) + rect(9, 2, 6, 6, 1) + 'M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3' + line(12, 12, 12, 8), 'Доступ із локальної мережі', 'LAN access', 'admin'),
    'info':         (circle(12, 12, 10) + line(12, 16, 12, 12) + dot(12, 8), 'Відомості', 'Information', 'admin'),
    # ---- feedback & states
    'clock':        (circle(12, 12, 10) + poly(12, 6, 12, 12, 16, 14), 'Потрібне поновлення / час', 'Renewal due / time', 'state'),
    'alert':        ('M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z' + line(12, 9, 12, 13) + dot(12, 17), 'Проблема', 'Problem', 'state'),
    'check':        (poly(4, 12, 9, 17, 20, 6), 'Готово', 'Done', 'state'),
    'x-circle':     (circle(12, 12, 10) + line(15, 9, 9, 15) + line(9, 9, 15, 15), 'Не вдалося', 'Failed', 'state'),
    'image-off':    (line(2, 2, 22, 22) + 'M10.4 10.4a2 2 0 1 1-2.8-2.8' + 'M13.5 13.5 6 21' + 'M18 12l3 3' + 'M3.6 3.6A2 2 0 0 0 3 5v14a2 2 0 0 0 2 2h14a2 2 0 0 0 1.4-.6' + 'M21 15V5a2 2 0 0 0-2-2H9', 'Ще немає зображення', 'No picture yet', 'state'),
    'loader':       (line(12, 2, 12, 6) + line(12, 18, 12, 22) + line(4.9, 4.9, 7.8, 7.8) + line(16.2, 16.2, 19.1, 19.1) + line(2, 12, 6, 12) + line(18, 12, 22, 12) + line(4.9, 19.1, 7.8, 16.2) + line(16.2, 7.8, 19.1, 4.9), 'Виконується', 'In progress', 'state'),
    'bell':         ('M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9' + 'M10.3 21a2 2 0 0 0 3.4 0', 'Сповіщення', 'Notifications', 'state'),
    # ---- generic
    'plus':         (line(12, 5, 12, 19) + line(5, 12, 19, 12), 'Додати', 'Add', 'generic'),
    'minus':        (line(5, 12, 19, 12), 'Зменшити', 'Decrease', 'generic'),
    'close':        (line(18, 6, 6, 18) + line(6, 6, 18, 18), 'Закрити', 'Close', 'generic'),
    'chevron-down': (poly(6, 9, 12, 15, 18, 9), 'Розгорнути', 'Expand', 'generic'),
    'chevron-right': (poly(9, 6, 15, 12, 9, 18), 'Далі', 'Next', 'generic'),
    'chevron-left': (poly(15, 6, 9, 12, 15, 18), 'Назад', 'Back', 'generic'),
    'more':         (dot(5, 12) + dot(12, 12) + dot(19, 12), 'Більше дій', 'More actions', 'generic'),
    'search':       (circle(11, 11, 8) + line(21, 21, 16.7, 16.7), 'Пошук', 'Search', 'generic'),
    'filter':       ('M22 3H2l8 9.5V19l4 2v-8.5z', 'Фільтр', 'Filter', 'generic'),
    'download':     ('M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4' + poly(7, 10, 12, 15, 17, 10) + line(12, 15, 12, 3), 'Експортувати / зберегти файл', 'Export / save file', 'generic'),
    'upload':       ('M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4' + poly(17, 8, 12, 3, 7, 8) + line(12, 3, 12, 15), 'Імпортувати / додати файл', 'Import / add file', 'generic'),
    'folder':       ('M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z', 'Папка', 'Folder', 'generic'),
    'edit':         ('M17 3a2.8 2.8 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5z', 'Редагувати', 'Edit', 'generic'),
    'play':         ('M6 3l14 9-14 9z', 'Запустити', 'Run', 'generic'),
    'save':         ('M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z' + poly(17, 21, 17, 13, 7, 13, 7, 21) + poly(7, 3, 7, 8, 15, 8), 'Зберегти', 'Save', 'generic'),
    'undo':         (poly(9, 14, 4, 9, 9, 4) + 'M20 20v-7a4 4 0 0 0-4-4H4', 'Скасувати зміни', 'Revert', 'generic'),
    'refresh':      ('M3 12a9 9 0 0 1 15.7-6L21 8' + 'M21 3v5h-5' + 'M21 12a9 9 0 0 1-15.7 6L3 16' + 'M8 16H3v5', 'Перевірити ще раз', 'Check again', 'generic'),
    'printer':      (poly(6, 9, 6, 2, 18, 2, 18, 9) + 'M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2' + rect(6, 14, 12, 8), 'Друкувати', 'Print', 'generic'),
    'language':     ('M5 8l6 6' + 'M4 14l6-6 2-3' + 'M2 5h12' + 'M7 2h1' + 'M22 22l-5-10-5 10' + 'M14 18h6', 'Мова', 'Language', 'generic'),
    'theme':        (circle(12, 12, 10) + 'M12 18a6 6 0 0 0 0-12z', 'Тема', 'Theme', 'generic'),
    'external':     ('M15 3h6v6' + line(10, 14, 21, 3) + 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6', 'Відкрити деталі', 'Open details', 'generic'),
    'eye':          ('M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7z' + circle(12, 12, 3), 'Переглянути', 'View', 'generic'),
}

GROUPS = [('nav', 'Навігація · Navigation'), ('lesson', 'Урок · Lesson'), ('control', 'Іспит і інтернет · Exam & internet'),
          ('power', 'Живлення · Power'), ('pcs', 'Комп’ютери · Computers'), ('grid', 'Мозаїка · Mosaic'),
          ('admin', 'Ключ і адміністрування · Key & admin'), ('state', 'Стани й відповіді · States & feedback'),
          ('generic', 'Загальні · Generic')]


def svg(d, size=24):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" '
            f'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="{d}"/></svg>\n')


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (d, uk, en, g) in ICONS.items():
        with open(os.path.join(OUT, name + '.svg'), 'w') as fh:
            fh.write(svg(d))
    with open(os.path.join(HERE, 'icons.json'), 'w') as fh:
        json.dump({k: v[0] for k, v in ICONS.items()}, fh)
    cards = []
    for g, title in GROUPS:
        items = ''.join(
            f'<figure title="{html.escape(name)}"><div class="g">{svg(d).strip()}</div><div class="g sm">{svg(d, 16).strip()}</div>'
            f'<figcaption><code>{name}</code><span>{html.escape(uk)}</span><span class="en">{html.escape(en)}</span></figcaption></figure>'
            for name, (d, uk, en, gg) in ICONS.items() if gg == g)
        cards.append(f'<h2>{title}</h2><div class="grid">{items}</div>')
    page = f'''<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LabControl icons</title><style>
:root{{--bg:#f6f7f9;--card:#fff;--fg:#16191d;--muted:#5b6470;--line:#d9dde3}}
@media (prefers-color-scheme:dark){{:root:not([data-theme=light]){{--bg:#111317;--card:#1a1d22;--fg:#eceef1;--muted:#a7afba;--line:#343a43}}}}
:root[data-theme=dark]{{--bg:#111317;--card:#1a1d22;--fg:#eceef1;--muted:#a7afba;--line:#343a43}}
body{{margin:0;padding:24px 16px;background:var(--bg);color:var(--fg);font:14px/1.45 Inter,system-ui,sans-serif}}
main{{max-width:1180px;margin:auto}} h1{{font-size:22px;margin:0 0 4px}} h2{{font-size:15px;margin:28px 0 10px;color:var(--muted)}}
.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:10px}}
figure{{margin:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px;display:grid;grid-template-columns:auto auto 1fr;gap:10px;align-items:center}}
.g{{display:flex}} .g svg{{display:block}} figcaption{{display:flex;flex-direction:column;font-size:12px;min-width:0}}
figcaption code{{font-size:11px;color:var(--muted)}} .en{{color:var(--muted)}} button{{font:inherit;padding:4px 10px;border-radius:6px;border:1px solid var(--line);background:var(--card);color:var(--fg);cursor:pointer}}
</style></head><body><main>
<h1>LabControl — action icon set ({len(ICONS)} icons)</h1>
<p style="color:var(--muted);max-width:820px">Drawn for LabControl on a 24×24 grid: 2 px stroke, round caps and joins, no fill — the
Lucide convention, so a Lucide icon (ISC licence) can be added later without looking foreign. Each icon is <b>one path geometry</b>
(no circles/rects), usable unchanged as Avalonia <code>StreamGeometry</code> and as an <code>SKPath</code> for the student overlay.
Shown at 24 px and 16 px. <button onclick="document.documentElement.dataset.theme=document.documentElement.dataset.theme==='dark'?'light':'dark'">Light / dark</button></p>
{''.join(cards)}
</main></body></html>'''
    with open(os.path.join(OUT, 'index.html'), 'w') as fh:
        fh.write(page)
    print(len(ICONS), 'icons')


if __name__ == '__main__':
    main()
