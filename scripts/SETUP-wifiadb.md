# Настройка беспроводного adb на второй рамке

Эта инструкция — для машины, которой **вторая рамка уже доверяет** (известная рабочая —
`tobalr-HP-EliteBook-8570p`). Её цель — доделать на второй рамке то же, что уже сделано на
первой: загнать ключ от главной машины (`MAIN_PC`) и включить персистентный adb по Wi-Fi,
чтобы дальше сетапить/деплоить рамку вообще без USB и без этой машины.

Первую рамку (`192.168.2.145:5555`) уже настроили; вторая сейчас `unauthorized` из-под
`MAIN_PC` — она принимает только ключи, которые ей «скормили».

## Как рамка авторизует adb

Рамка не показывает «разрешить отладку?» и принимает только ключи, лежащие в:

```
/data/misc/adb/adb_keys
```

Проверить, какие ключи уже скормлены (с машины, которой рамка доверяет):

```bash
adb shell su -c "cat /data/misc/adb/adb_keys"
```

Там две строки (видны на первой рамке):
```
... tobalr@tobalr-HP-EliteBook-8570p
... Uladz@MAIN_PC
```

## Шаг 1. Скормить главной машине (MAIN_PC) ключ

Ключ, который надо добавить (`Uladz@MAIN_PC` = публичный ключ машины MAIN_PC):

```
QAAAANGM7wPPsyd+dvTmSzb6td6ofGo8fpflP4lAIQq7nfPJVZCUSH4jPP6ZBZ8narZNoN9Dt4hLH85JBnft6v/hmJYAS72NPjH/nukIyzI+O0yeiE6Ny/cSrG82Oeg6y3nCUdqlWbOSjjLylyL1v0a+T4khwaMu5mr+go0eRw19fmSEal833LeyjPnAQrvyIy7195RVr5aqNapiQJxxBaPwj9vnkhueUPAnldrpfudyO+LWpAF9LCWmmZYlZwaVcoT6QY4GLDbjP7UPxUiYEHATkVxXgGxmLnDIRSOAcLeBdUTYjS+X7PBXJIgJY3DPnJZ1utFX+r7RuEGHntL6Zoumalo74WbS2vGhoh3/Zx4jYQdHxji7bE31OWcYI1HkgkQvhIXSbyswl8vAovojdervVZJHKQfDIYl85yuKYPAXcM4llJt6L/Loz8S6r7S6NLKCvJoFwWNxt9l5eUmbWOpTglVEtgmqpJLqXZXYf1Idvj7AT70RHi3AeQTCs59cTbbj6+2+wVi9N/nmqfyeSEqPB1I9iY0yxuyGpxggch7Z43CQhZVNxMeS2yxbF86UC9uWvp08IC14Y5UlQSOcw6+HNpQk2kyWSnINlCJ812gl4WTnAL1jDO4f7NUyz8nX8vaY7Y5d7o/T6k5+F6hYhYMtDsQBg8d0HI63Yky8Y8QmktoeWVjqBgEAAQA= Uladz@MAIN_PC
```

Добавить на рамку (рамка подключена по USB и `device` на этой машине):

```bash
adb root                          # userdebug-сборка позволяет
adb shell su -c "echo '<КЛЮЧ_ВЫШЕ>' >> /data/misc/adb/adb_keys"
adb shell su -c "chown root:root /data/misc/adb/adb_keys"
adb shell su -c "stop adbd; start adbd"   # чтобы adbd перечитал ключ
```

После этого рамка станет видна и с `MAIN_PC` (в `adb devices` будет `device` вместо `unauthorized`).

## Шаг 2. Включить adb-tcp (один раз, по USB)

```bash
adb tcpip 5555
adb connect <IP_РАМКИ>:5555
```

IP второй рамки: `adb shell ip addr show wlan0 | grep 'inet '` (пока она в USB).

## Шаг 3. Поставить персистентный init-скрипт (чтобы adb-tcp переживал ребут)

Файл `scripts/wifiadb.rc` (уже в репозитории) кладём в `/system/etc/init/`:

```bash
adb root
adb push scripts/wifiadb.rc /data/local/tmp/wifiadb.rc
adb shell su -c "mount -o rw,remount /system \
  && cp /data/local/tmp/wifiadb.rc /system/etc/init/wifiadb.rc \
  && chown root:root /system/etc/init/wifiadb.rc \
  && chmod 644 /system/etc/init/wifiadb.rc \
  && mount -o ro,remount /system && echo OK"
```

Содержимое `scripts/wifiadb.rc`:

```
on property:sys.boot_completed=1
    setprop service.adb.tcp.port 5555
    stop adbd
    start adbd
```

## Шаг 4. Проверить на ребуте

```bash
adb reboot
# подождать ~40-60с, затем:
adb connect <IP_РАМКИ>:5555
adb devices     # должен быть 192.168.2.x:5555 device
```

Если рамка поднялась по Wi-Fi после ребута — персистентный adb-tcp работает, USB больше не нужен.

## Дальше (с MAIN_PC)

```bash
adb connect <IP_РАМКИ>:5555
FRAME=<IP_РАМКИ>:5555 bash scripts/deploy.sh        # одиночный деплой
bash scripts/deploy-lan.sh                           # на все найденные рамки
```

## Важно

- `adb tcpip` и init-запись требуют root (`adb root` или `su`) — у этих рамок `ro.debuggable=1`, root доступен.
- `/system` в read-only: сначала `mount -o rw,remount /system`, в конце вернуть `ro`.
- Если с подключённой второй рамкой видно несколько устройств (например, телефон на USB), одиночный деплой делай только с явным `FRAME=<IP>:5555`.
- Никогда не выбирай в recovery wipe/reset — только сам вход.
