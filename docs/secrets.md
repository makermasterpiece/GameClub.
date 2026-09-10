# Secrets and configuration

Пароли игроков и сотрудников принимаются только при разрешённом создании/смене пароля и login; первый employee — через явный CLI bootstrap. Они хешируются ASP.NET Core Identity PBKDF2 и не сохраняются/логируются в plaintext. `PasswordHash` хранится только в PostgreSQL и не входит ни в один response DTO. Bootstrap password нельзя оставлять в обычном appsettings или постоянном окружении процесса.

## Public configuration

Следующие значения допустимы в `appsettings.json`:

- `Server:BaseUrl`;
- `Station:Name`;
- timeouts/log levels;
- `Security:AllowInsecureDevelopmentHttp` (default `false`);
- пути к credential/replay files;
- пути к server PEM/PFX (сам файл находится вне repository);
- путь к ASP.NET Core Data Protection key ring (сами keys — secrets);
- JWT issuer (не secret).

## Secrets

Следующие значения нельзя commit-ить или помещать в обычный `appsettings.json`:

- PostgreSQL password / полный production connection string;
- raw enrollment token;
- station secret;
- ECDSA private key или PFX;
- PFX password;
- ASP.NET Core Data Protection key ring;
- full JWT.
- `Bootstrap__Password` и любые реальные пароли игроков/сотрудников.

Используйте environment variables, OS secret store, container secret или deployment secret provider:

| Purpose | Configuration key / location |
|---|---|
| PostgreSQL | `ConnectionStrings__GameClubDb` |
| One-time first Administrator | `Bootstrap__Username`, `Bootstrap__Password` только в окружении запуска `--bootstrap-admin`, затем очистить |
| One-time Agent enrollment | `Security__EnrollmentToken` в окружении первого запуска Agent |
| Server PEM path | `Security__CommandSigningPrivateKeyPath` |
| Server X.509 PFX path | `Security__CommandSigningCertificatePath` |
| PFX password | `Security__CommandSigningCertificatePassword` только через secret provider |
| Data Protection key ring | `Security__DataProtectionKeysPath`; вне repository, на Windows keys защищаются DPAPI LocalMachine |
| Agent identity | DPAPI file `%ProgramData%\GameClub\Agent\credentials.dat` |
| Server Data Protection | OS-protected key ring текущего service identity или внешний protected key repository |

В development ephemeral ECDSA key допустим, но меняется после restart. Для repeatable enrollment используйте внешний постоянный PEM с ACL только для server service identity/Administrators. Обычный restart не является причиной генерации нового ключа: он инвалидирует pinned trust станций и выданные JWT.

## Repository exclusions

`.gitignore` исключает `.env`, PFX/P12/private key patterns, DPAPI credential file, SQLite state DB, build outputs и local `work`. Перед commit дополнительно проверяйте:

```powershell
git diff --cached
git grep -n -I -E "POSTGRES_PASSWORD=.+|BEGIN.*PRIVATE KEY|StationSecret|EnrollmentToken"
```

Совпадения имён DTO/config допустимы; реальные значения — нет.

## Rotation and incident response

При подозрении на compromise станции:

1. Administrator вызывает `POST /api/stations/{id}/credentials/revoke` со своим Employee JWT;
2. остановите Agent;
3. расследуйте/переустановите station OS;
4. удалите старый DPAPI credential file;
5. создайте новый одноразовый enrollment token;
6. выполните re-enrollment;
7. проверьте `SecurityAuditEvents`.

При compromise server signing private key требуется заменить key и заново enrollment всех Agent, потому что public key pin хранится в их DPAPI credential.
