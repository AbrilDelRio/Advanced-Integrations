# Advanced Integrations – Dataverse plugins

| Proyecto | Ensamblado | Rol |
|---|---|---|
| `AutoregisterPlugin` | `AutoregisterPlugin.dll` | Crea/actualiza el `sdkmessageprocessingstep` al guardar un `mfd_advancedmapping` / `mfd_advancedintegrationtrigger`. |
| `AdvancedIntegrationPlugin` | `CreateAdvancedIntegrationPlugin.dll` | Ejecuta la sincronización cuando se dispara el step generado. |

## Run Under User (`mfd_rununderuser`)

Lookup a `systemuser` en `mfd_advancedmapping`. AutoRegisterPlugin lo copia en
`sdkmessageprocessingstep.impersonatinguserid`:

- Con valor → el step corre como ese usuario / service account (`context.UserId`).
- Vacío o limpiado → `impersonatinguserid = null` → **Calling User**.
- Usuario deshabilitado o inexistente → el guardado del mapping falla con un mensaje claro.

AdvancedIntegrationPlugin crea el servicio con `context.UserId` (el usuario impersonado), escribe `Running as ...` en el Plugin Trace Log y agrega un `WARNING` al log de integración solo si el step no coincide con el mapping.

## Compilar

```bash
dotnet build AutoregisterPlugin/AutoregisterPlugin/AutoregisterPlugin.csproj -c Release
dotnet build AdvancedIntegrationPlugin/AdvancedIntegrationPlugin.csproj -c Release
```

(o *Build → Release* en Visual Studio). Target `net462`, ensamblados firmados con los `.snk` incluidos.

## Desplegar (Plugin Registration Tool)

1. **Update** de cada assembly con el DLL de `bin/Release/net462/` (no registrar uno nuevo:
   se conservan los steps existentes).
2. Step de AutoRegisterPlugin en **Update** de `mfd_advancedmapping`:
   - Agregar `mfd_rununderuser` a la **PreImage** (`PreImage`).
   - Si el step tiene *Filtering Attributes*, agregar `mfd_rununderuser`.
3. El usuario con el que corre AutoRegisterPlugin debe poder escribir `sdkmessageprocessingstep`
   e impersonar (System Administrator, o rol con *Act on Behalf of Another User*).
4. El usuario elegido en Run Under User necesita licencia/rol con permisos sobre las tablas
   de origen y destino, `mfd_advancedmapping`, `mfd_advancedintegrationlog` y las de notificación.
5. Steps ya existentes: re-guardar cada mapping (o setear el campo) para sincronizar el step.
6. Probar: asignar un usuario, verificar en PRT que el step muestre *Run in User's Context* = ese
   usuario, disparar el evento y revisar `createdby` / `createdonbehalfby` y el log. Limpiar el
   campo y verificar que vuelva a *Calling User*.

## PCF `AdvancedIntegrationconfig`

Configurador visual de los mappings. Guarda con la Web API, así que dispara los mismos plugins
que el formulario. En *General → Run under user* se elige el usuario (`mfd_RunUnderUser@odata.bind`);
*Clear* lo vacía y el step vuelve a Calling User.

Desplegar: `npm run build` y luego `pac pcf push` (o reempaquetar la solución). La versión del
manifiesto debe subir en cada despliegue para que Dataverse tome el control nuevo.
