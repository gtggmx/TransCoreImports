USE [GmxRaw]
GO

-- New columns for the morning copy from the CPC server (nullable, metadata-only,
-- existing rows stay NULL). Axel keeps CollectorAxles; IVISAxles gets its own column.
ALTER TABLE [dbo].[Data_1220] ADD
    [TollFull]  [float]    NULL,
    [TollDay]   [datetime] NULL,
    [IVISAxles] [int]      NULL
GO
