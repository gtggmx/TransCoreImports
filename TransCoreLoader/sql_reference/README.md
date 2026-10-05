# SQL the TransCoreLoader depends on

Snapshot of the database objects as of 2026-10-05. When one of them changes, re-export it
over the file here (`git diff` then shows exactly what changed) and check the "Loader code
that relies on it" column.

| File | Where it lives | Loader code that relies on it |
|---|---|---|
| `CPC.uspr_trn_CPC_TransactionDetails_withETCNum.sql` | CPC server 10.66.170.53, DB `CPC` | `StreamSourceAsync`: parameter names, and the result columns TransDate, Shift, Lane, VehClass, TollFull, TollCharged, TollCollected, IVISAxles, CollectorAxles, TransType, OrgName, TollDay, LaneGroupName |
| `GmxRaw.SP_DATA_1220_INSERT.sql` | 192.168.150.73 `GmxRaw` | Reference only (the loader no longer calls it). `PlazaMatcher.Find` = its PlazaID lookup; `BuildUfm` = its UFM_CALC; rows with no match are skipped |
| `GmxRaw.SP_CREATE_IMPORT_SESSION.sql` | .73 `GmxRaw` | `CreateSessionAsync` (SessionID is the RETURN value) |
| `GmxRaw.SP_UPDATE_IMPORT_SESSION.sql` | .73 `GmxRaw` | `FinishSessionAsync` (params `@SESSION_ID`, `@NOF_FILES`; note the object is named SP_UPDATE_IMPORT_SESSION but the CREATE inside says SP_UPDATE_SESSION) |
| `GmxRaw.SP_INS_IMPORT_FILES.sql` | .73 `GmxRaw` | `InsertFileRowAsync`: revision handling, marks the previous revision 99; FileID read back by `ReadLatestFileIdAsync` |
| `GmxRaw.SP_UPDATE_IMP_FILES.sql` | .73 `GmxRaw` | `FinishFileRowAsync` (OldStatus 1 -> 2) |
| `GmxRaw.SP_GET_SEQ.sql` | .73 `GmxRaw` | Called by SP_CREATE_IMPORT_SESSION |
| `Data_1220_alter.sql` | .73 `GmxRaw` | Adds TollFull, TollDay, IVISAxles; `EnsureNewColumnsExistAsync` refuses to load without them |

Not exported (tables): `GmxParameters.dbo.GantryLaneConversionTable` and
`GmxParameters.dbo.PlazaDefinitions` are read at the start of every run.

Re-export example (PowerShell, credentials from the git-ignored appsettings.local.json):
`sqlcmd ... -y 0 -Q "SET NOCOUNT ON; SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.SP_DATA_1220_INSERT'));"`
