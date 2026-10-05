-- Create date: 3/12/2024
-- Description:	Update Imported File Values
-- =============================================
CREATE PROCEDURE [dbo].[SP_UPDATE_IMP_FILES] 
	-- Add the parameters for the stored procedure here
	@SessionID bigint
	,@ImportFileName nvarchar(256)
    ,@FileType nvarchar(64)
    ,@FileSize bigint = 0
    ,@IntervalBegin datetime2(7) = NULL
    ,@IntervalEnd datetime2(7) = NULL
    ,@LinesRead int = 0
    ,@ProcessStart datetime2(7) = NULL
    ,@ProcessEnd datetime2(7) = NULL
	,@OldStatus int = 1
    ,@Status int = 0
    ,@ProcHost nvarchar(256) = 0
    ,@ProcUser nvarchar(256) = 0
    ,@ReadErrors int = 0
    ,@WriteErrors int = 0
	,@FileLastUpdate_UTC bigint = 0
	,@ImportFilePath nvarchar(256) = NULL

AS
BEGIN

	SET NOCOUNT ON;

	DECLARE @ImpFileID bigint 

	IF @FileType = '2552M' 
	BEGIN
		SELECT @FileType = '2552'
	END

IF @FileType IN ('5695','QFREEVTOL','QFREEAMEND','REJECTED')
BEGIN
	SELECT 
		@ImpFileID = FileID
	FROM
		[dbo].[ProcessedFiles] (nolock)
	WHERE
		[ImportFileName] = @ImportFileName
		AND [FileType] = @FileType
		AND Status = @OldStatus
END

UPDATE 
	[dbo].[ProcessedFiles]
   SET 
      [FileSize] = @FileSize
      ,[IntervalBegin] = @IntervalBegin
      ,[IntervalEnd] = @IntervalEnd
      ,[LinesRead] = @LinesRead
      ,[ProcessStart] = @ProcessStart
      ,[ProcessEnd] = @ProcessEnd
      ,[Status] = @Status
      ,[ProcHost] = @ProcHost
      ,[ProcUser] = @ProcUser
      ,[ReadErrors] = @ReadErrors
      ,[WriteErrors] = @WriteErrors
      ,[FileLastUpdate_UTC] = @FileLastUpdate_UTC
      ,[ImportFilePath] = @ImportFilePath
 WHERE 
	  [SessionID] = @SessionID
      AND [ImportFileName] = @ImportFileName
      AND [FileType] = @FileType
	  AND Status = @OldStatus

IF @FileType IN ('QFREEVTOL')
BEGIN
	EXEC SP_UPDATE_TRANSIMPORT_STATS @FileID = @ImpFileID
END
IF @FileType IN ('QFREEAMEND')
BEGIN
	EXEC SP_UPDATE_AMENDIMPORT_STATS @FileID = @ImpFileID
END
IF @FileType IN ('5695')
BEGIN
	EXEC SP_UPDATE_IVFIMPORT_STATS @FileID = @ImpFileID
END
IF @FileType IN ('REJECTED')
BEGIN
	EXEC SP_UPDATE_REJECTEDIMPORT_STATS @FileID = @ImpFileID
END
END
