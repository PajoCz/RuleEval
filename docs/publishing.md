# Publishing RuleEval NuGet Packages

## Účel dokumentu

Tento dokument popisuje celý proces vydávání NuGet balíčků knihovny RuleEval na [nuget.org](https://www.nuget.org/).

**GitHub Actions je hlavní a doporučený způsob publishe.** Je automatický, auditovatelný a nevyžaduje, aby měl vývojář lokálně nastavený API klíč. Lokální PowerShell skript (`scripts/publish-nuget.ps1`) existuje jako doplněk pro ruční build/pack/publish nebo pro otestování release procesu bez GitHub Actions.

---

## Doporučený způsob vydání

| Způsob | Kdy použít |
|---|---|
| **GitHub Actions (tag)** | Standardní release nové verze |
| **GitHub Actions (workflow_dispatch)** | Ruční re-publish nebo oprava |
| **Lokální skript** | Testování procesu, urgentní patch, prostředí bez CI |

---

## Verzování

### Jeden zdroj pravdy

Výchozí verze je definována v `Directory.Build.props`:

```xml
<Version>0.1.0</Version>
```

Tato hodnota řídí:
- **NuGet package version** (název `.nupkg` souboru a metadata balíčku)
- **AssemblyVersion** a **FileVersion** (hodnoty vložené do DLL)
- **InformationalVersion** (může obsahovat i suffíx jako `-preview`)

### Verze při releasu

Při vydání nové verze **neupravuj** `Directory.Build.props` ručně. Místo toho:

1. Vytvoř Git tag ve formátu `vX.Y.Z` (viz sekce [Release přes GitHub Actions](#release-přes-github-actions)).
2. Workflow z tagu automaticky odvodí verzi `X.Y.Z` a předá ji přes `/p:Version=X.Y.Z` do `dotnet build` a `dotnet pack`.

`Directory.Build.props` slouží jako výchozí verze pro lokální development buildy (bez tagu), nikoli jako autoritativní release verze.

### Schéma číslování

| Typ změny | Příklad | Kdy |
|---|---|---|
| Patch (`Z`) | `0.1.0` → `0.1.1` | Oprava chyby, bez API změn |
| Minor (`Y`) | `0.1.1` → `0.2.0` | Nová funkce, zpětně kompatibilní |
| Major (`X`) | `0.2.0` → `1.0.0` | Breaking change nebo první stable release |

### Jak vznikne verze DLL a balíčku

```
git tag v0.2.0
       ↓
GitHub Actions: VERSION="0.2.0"
       ↓
dotnet build /p:Version=0.2.0   → DLL s AssemblyVersion=0.2.0
dotnet pack  /p:Version=0.2.0   → RuleEval.0.2.0.nupkg + RuleEval.0.2.0.snupkg
       ↓
dotnet nuget push → nuget.org
```

---

## Předpoklady

- Nainstalované [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- Účet na [nuget.org](https://www.nuget.org/) s oprávněním publikovat balíčky
- Vytvořený NuGet API klíč (viz sekce níže)
- Pro GitHub Actions: nastavený secret `NUGET_API_KEY` v repozitáři

---

## Jak nastavit `NUGET_API_KEY`

> **Bezpečnost:** API klíč nikdy necommituj do repozitáře. Neukládej ho do skriptů, `.csproj`, `.json` ani `.config` souborů v repu.

### Vytvoření API klíče

1. Přihlas se na [nuget.org](https://www.nuget.org/).
2. Přejdi do **Account → API Keys**.
3. Vytvoř nový klíč s oprávněním `Push` pro balíčky s prefixem `RuleEval.*`.

### Nastavení pro GitHub Actions (doporučeno)

1. V repozitáři přejdi na **Settings → Secrets and variables → Actions**.
2. Klikni na **New repository secret**.
3. Název: `NUGET_API_KEY`, hodnota: tvůj API klíč.

### Nastavení pro lokální PowerShell

**Jen pro aktuální session (dočasné):**
```powershell
$env:NUGET_API_KEY = "your-api-key-here"
```

**Trvale pro aktuálního uživatele:**
```powershell
[Environment]::SetEnvironmentVariable("NUGET_API_KEY", "your-api-key-here", "User")
```
> Po `SetEnvironmentVariable` je nutné otevřít nový terminál (nebo restartovat Visual Studio), aby nová hodnota byla načtena.

### Nastavení pro CMD

**Jen pro aktuální session:**
```cmd
set NUGET_API_KEY=your-api-key-here
```

**Trvale (zápisem do registru):**
```cmd
setx NUGET_API_KEY "your-api-key-here"
```
> Po `setx` je potřeba otevřít nový terminál.

### Nastavení přes Windows GUI

1. Otevři **System Properties → Advanced → Environment Variables**.
2. V sekci **User variables** přidej novou proměnnou `NUGET_API_KEY`.
3. Restartuj terminál nebo Visual Studio.

---

## Release přes GitHub Actions

Toto je standardní a doporučený způsob vydání nové verze.

### Postup krok za krokem

1. **Ujisti se, že main branch je v pořádku** – CI workflow (`ci.yml`) musí procházet.

2. **Vytvoř a push Git tag:**
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. **Workflow se spustí automaticky** na adrese:
   `https://github.com/PajoCz/RuleEval/actions/workflows/publish.yml`

4. **Workflow provede:**
   - Checkout s plnou historií (pro SourceLink)
   - Určení verze z tagu (`v0.2.0` → `0.2.0`)
   - `dotnet restore`
   - `dotnet build -c Release /p:Version=0.2.0`
   - Unit testy
   - Integrační testy
   - `dotnet pack -c Release /p:Version=0.2.0`
   - Upload `.nupkg` a `.snupkg` jako GitHub Actions artifact
   - `dotnet nuget push` na nuget.org

5. **Ověř publikování** na [nuget.org](https://www.nuget.org/profiles/PajoCz).

### Ruční spuštění (workflow_dispatch)

Pokud chceš spustit publish bez tagu (např. re-publish nebo test):

1. Přejdi na **Actions → publish → Run workflow**.
2. Zadej verzi, např. `0.2.0`.
3. Klikni **Run workflow**.

---

## Lokální publish (skript)

> Lokální publish je doplněk, ne hlavní release cesta. Použij ho jen tehdy, když GitHub Actions není k dispozici nebo chceš otestovat release proces lokálně.

### Požadavky

- Nastavená proměnná prostředí `NUGET_API_KEY`
- Přístup k internetu (nuget.org)

### Použití

**Základní publish s verzí:**
```powershell
.\scripts\publish-nuget.ps1 -Version 0.2.0
```

**Publish bez testů (rychlé ověření procesu):**
```powershell
.\scripts\publish-nuget.ps1 -Version 0.2.0 -SkipTests
```

**Nastavení API klíče a publish v jednom kroku:**
```powershell
$env:NUGET_API_KEY = "your-api-key-here"
.\scripts\publish-nuget.ps1 -Version 0.2.0
```

**Publish s verzí z `Directory.Build.props` (bez přepsání):**
```powershell
.\scripts\publish-nuget.ps1
```

Skript uloží výstupy do `artifacts/nupkgs-local/`. Tento adresář je v `.gitignore`.

---

## Co interně probíhá

```
dotnet restore RuleEval.sln
    ↓
dotnet build RuleEval.sln -c Release /p:Version=X.Y.Z
    ↓
dotnet test  (unit + integration)
    ↓
dotnet pack  RuleEval.sln -c Release /p:Version=X.Y.Z -o artifacts/nupkgs
    → RuleEval.X.Y.Z.nupkg           (library)
    → RuleEval.X.Y.Z.snupkg          (symbols)
    → RuleEval.Abstractions.X.Y.Z.nupkg
    → ... (všechny packable projekty)
    ↓
dotnet nuget push *.nupkg --skip-duplicate
    → nuget.org přijme .nupkg i companion .snupkg
```

---

## Debugging a symboly

- Release build může mít JIT optimalizace – stepování nemusí být vždy stejně pohodlné jako u Debug buildu.
- Každý `.nupkg` má companion `.snupkg` se symboly (PDB) – tyto jsou automaticky pushnuty spolu s hlavním balíčkem.
- [Source Link](https://github.com/dotnet/sourcelink) je nakonfigurován v `Directory.Build.props` (`Microsoft.SourceLink.GitHub`). Debugger v Visual Studiu může automaticky stáhnout zdrojový kód ze GitHubu.
- Aby Source Link fungoval, musí být commit dostupný ve veřejném repozitáři v době, kdy debugger zdrojový kód požaduje.

---

## Bezpečnost

- **Nikdy** necommituj API klíč do repozitáře.
- Neukládej API klíč do skriptů, `.csproj`, `.json`, `.config` ani jiných souborů v repu.
- API klíč předávej výhradně přes proměnnou prostředí `NUGET_API_KEY` nebo GitHub secret.
- GitHub secret je šifrovaný a nikdy se nezobrazí v lozích workflow.
- Pokud dojde k úniku API klíče, okamžitě ho revokuj na nuget.org a vytvoř nový.

---

## Troubleshooting

### `NUGET_API_KEY` není nastavena
```
ERROR: NUGET_API_KEY environment variable is not set.
```
Nastav proměnnou prostředí podle sekce [Jak nastavit `NUGET_API_KEY`](#jak-nastavit-nuget_api_key).

### Balíček s touto verzí už existuje
```
Response status code does not indicate success: 409 (Conflict)
```
Verze na nuget.org je immutable. Zvyš číslo verze (`Z` nebo `Y`) a vydej nový tag.  
Pokud bylo použito `--skip-duplicate`, workflow tento stav ignoruje.

### `dotnet restore` selže
- Zkontroluj připojení k internetu.
- Zkontroluj `Directory.Packages.props` a verze balíčků.
- Spusť `dotnet nuget locals all --clear` a zkus znovu.

### Testy selžou
Chyba je zobrazena v logu. Oprav ji, commitni a vytvoř nový tag.  
Poznámka: `--skip-duplicate` v push kroku je v pořádku (ignoruje již publikovanou verzi), ale nevynechávej testy ani nepoužívej `--no-build` jako obejití selhání buildu.

### `dotnet pack` selže
- Zkontroluj, zda build proběhl bez chyb.
- Zkontroluj metadata v `Directory.Build.props` (zejm. `PackageLicenseExpression`, `RepositoryUrl`).

### `dotnet nuget push` selže
- Ověř platnost API klíče na nuget.org.
- Zkontroluj, zda klíč má oprávnění `Push` pro balíčky `RuleEval.*`.
- Zkontroluj, zda není dosažen rate limit nuget.org.

### Verze je špatně zadaná
Pokud tag nevypadá jako `vX.Y.Z` (nebo input v workflow_dispatch není ve tvaru `X.Y.Z`), workflow selže s chybou:
```
ERROR: Version '...' does not match expected format X.Y.Z
```
Oprav tag nebo vstup a spusť znovu.

### Source Link nebo symboly se nechovají správně
- Zkontroluj, že commit odpovídá tagu v GitHubu (fetch-depth: 0 v workflow).
- Zkontroluj, že v `Directory.Build.props` je `EmbedUntrackedSources=true`.
- V Visual Studiu povol **Tools → Options → Debugging → Enable Source Link Support**.

---

## Příklady použití

### Vydání nové verze přes tag
```bash
# Ujisti se, že jsi na main a vše je pushnuté
git checkout main
git pull origin main

# Vytvoř a push tag
git tag v0.2.0
git push origin v0.2.0

# Workflow se spustí automaticky na GitHubu
```

### Ruční publish se zadanou verzí (workflow_dispatch)
1. Přejdi na **Actions → publish → Run workflow**
2. Zadej `0.2.0` do pole Version
3. Klikni **Run workflow**

### Lokální publish bez testů
```powershell
$env:NUGET_API_KEY = "your-api-key-here"
.\scripts\publish-nuget.ps1 -Version 0.2.0 -SkipTests
```

### Nastavení `NUGET_API_KEY` v aktuálním PowerShell okně
```powershell
$env:NUGET_API_KEY = "oy2abc...xyz"
# Ověření:
$env:NUGET_API_KEY
```
