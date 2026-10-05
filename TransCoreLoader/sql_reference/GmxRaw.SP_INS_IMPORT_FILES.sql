-- Create date: 
-- Description:	Insert Update Files Imported
-- =============================================
CREATE PROCEDURE [dbo].[SP_INS_IMPORT_FILES] 
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
           ,@Status int = 0
           ,@ProcHost nvarchar(256) = 0
           ,@ProcUser nvarchar(256) = 0
           ,@ReadErrors int = 0
           ,@WriteErrors int = 0
		   ,@FileLastUpdate_UTC bigint = 0
		   ,@ImportFilePath nvarchar(256) = NULL
AS
BEGIN
	DECLARE @MAX_REV INT
	DECLARE @OldFileID bigint
	SELECT 
		 @MAX_REV = MAX(Revision)
	FROM 
		[dbo].[ProcessedFiles] (nolock)
	WHERE
			[ImportFileName] = @ImportFileName
		AND [FileType] = @FileType
	SELECT 
		 @OldFileID = FileID
	FROM 
		[dbo].[ProcessedFiles] (nolock)
	WHERE
			[ImportFileName] = @ImportFileName
		AND [FileType] = @FileType
		AND Revision = @MAX_REV
		
	
	DECLARE @FILE_SEQ bigint
	SET @FILE_SEQ = NEXT VALUE FOR dbo.SeqProcFiles

IF ISNULL(@MAX_REV,-1) = -1
BEGIN
	INSERT INTO [dbo].[ProcessedFiles]
           ([SessionID]
		   ,[ImportFileName]
           ,[FileType]
           ,[FileSize]
           ,[IntervalBegin]
           ,[IntervalEnd]
           ,[LinesRead]
           ,[ProcessStart]
           ,[ProcessEnd]
           ,[Status]
		   ,[Revision] 
           ,[ProcHost]
           ,[ProcUser]
		   ,[ReadErrors]
		   ,[WriteErrors]
		   ,[FileLastUpdate_UTC]
		   ,[ImportFilePath]
		   ,[FileID])
     VALUES
           (
		   @SessionID
		   ,@ImportFileName
           ,@FileType
           ,@FileSize
           ,@IntervalBegin
           ,@IntervalEnd
           ,@LinesRead
           ,@ProcessStart
           ,@ProcessEnd
           ,@Status
		   ,1
           ,@ProcHost
           ,@ProcUser
		   ,@ReadErrors
		   ,@WriteErrors
		   ,@FileLastUpdate_UTC
		   ,@ImportFilePath
		   ,@FILE_SEQ
		   )
END
ELSE
BEGIN
	UPDATE
		[dbo].[ProcessedFiles]
	SET 
		[Status] = 99
	WHERE
		[ImportFileName] = @ImportFileName
	AND [FileType] = @FileType
	AND [Revision] = @MAX_REV
	IF @FileType = 'QFREEAMEND'
	BEGIN
		DELETE FROM GmxRaw.dbo.QFREE_AMEND_X WHERE ImportFileID = @OldFileID
	END
	INSERT INTO [dbo].[ProcessedFiles]
           ([SessionID]
		   ,[ImportFileName]
           ,[FileType]
           ,[FileSize]
           ,[IntervalBegin]
           ,[IntervalEnd]
           ,[LinesRead]
           ,[ProcessStart]
           ,[ProcessEnd]
           ,[Status]
		   ,[Revision] 
           ,[ProcHost]
           ,[ProcUser]
		   ,[ReadErrors]
		   ,[WriteErrors]
		   ,[FileLastUpdate_UTC]
		   ,[ImportFilePath]
		   ,[FileID])
     VALUES
           (
		   @SessionID
		   ,@ImportFileName
           ,@FileType
           ,@FileSize
           ,@IntervalBegin
           ,@IntervalEnd
           ,@LinesRead
           ,@ProcessStart
           ,@ProcessEnd
           ,@Status
		   ,@MAX_REV+1
           ,@ProcHost
           ,@ProcUser
		   ,@ReadErrors
		   ,@WriteErrors
		   ,@FileLastUpdate_UTC
		   ,@ImportFilePath
		   ,@FILE_SEQ
		   )
END
END
