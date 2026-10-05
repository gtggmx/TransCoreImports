-- Create date: 1/30/2024
-- Description:	Procedure to push 1310 data to db
-- =============================================
CREATE PROCEDURE [dbo].[SP_DATA_1220_INSERT] 
	-- Add the parameters for the stored procedure here
	@ImportFileID bigint,
	@ImportFileLineNumber int,
	@TransDate datetime2(7),
	@Plaza nvarchar(64),
	@LaneGroup nvarchar(32),
	@Gantry int,
	@Lane int,
	@Shift int,
	@Class int,
	@TollExp float,
	@TollColl float,
	@TollType nvarchar(64),
	@Axel int,
	@ForceUPD nvarchar(8) = 'NO'
AS
BEGIN
	declare @new_iv_end datetime2;		-- New Interval End
	declare @old_iv_end datetime2;		-- Old Interval End
	DECLARE @OLD_FILE_ID BIGINT			-- Old FileID
	DECLARE @PLAZA_ID int = -1			-- Plaza ID

	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;

	SELECT @PLAZA_ID = ISNULL(PLAZAID,-1) FROM
	( 
		SELECT DISTINCT gantry,PLAZA,PLAZAID,LaneGroup,CoreRoad FROM GmxParameters.[dbo].[GantryLaneConversionTable] (nolock)
	) glx 
	WHERE
		glx.gantry = @Gantry and
		(
		@Plaza = glx.Plaza or
		@LaneGroup = glx.LaneGroup OR
		(
			CASE 
				WHEN @LaneGroup like '%924%' THEN 'SR924'
				WHEN @LaneGroup like '%112%' THEN 'SR112'
				WHEN @LaneGroup like '%874%' THEN 'SR874'
				WHEN @LaneGroup like '%836%' THEN 'SR836'
				WHEN @LaneGroup like '%878%' THEN 'SR878'
			END = glx.CoreRoad OR
			CASE
				WHEN @Plaza like '%924%' THEN 'SR924'
				WHEN @Plaza like '%112%' THEN 'SR112'
				WHEN @Plaza like '%874%' THEN 'SR874'
				WHEN @Plaza like '%836%' THEN 'SR836'
				WHEN @Plaza like '%878%' THEN 'SR878'
			END  =glx.CoreRoad)
		)
IF(@PLAZA_ID != -1)
BEGIN
DECLARE @UFM_CALC nvarchar(32)
DECLARE @UTC_TIME DATETIME2(7) 
SELECT @UTC_TIME = @TransDate AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' 
SELECT @UFM_CALC = 
	FORMAT(pd.ORG_ID,'0#')+
	FORMAT(@Lane,'0#')+
	FORMAT(DATEPART(YEAR,@UTC_TIME)-2000,'0#')+
	FORMAT(DATEPART(MONTH,@UTC_TIME),'0#')+
	FORMAT(DATEPART(DAY,@UTC_TIME),'0#')+
	FORMAT(DATEPART(HOUR,@UTC_TIME),'0#')+
	FORMAT(DATEPART(MINUTE,@UTC_TIME),'0#')+
	FORMAT(DATEPART(SECOND,@UTC_TIME),'0#')+
	FORMAT(DATEPART(MILLISECOND,@UTC_TIME),'00#')
FROM
	GmxParameters.dbo.PlazaDefinitions (nolock) pd
WHERE
	pd.PLAZA_ID = @PLAZA_ID

BEGIN TRY
	INSERT INTO [dbo].[Data_1220]
	(
		[ImportFileID]
		,[ImportFileLineNumber]
		,[TransDate]
		,[Plaza]
		,[LaneGroup]
		,[Gantry]
		,[Lane]
		,[PlazaID]
		,[Shift]
		,[Class]
		,[TollExp]
		,[TollColl]
		,[TollType]
		,[Axel]
		,[UFM_CALC]
	)
	VALUES
	 (
		@ImportFileID,
		@ImportFileLineNumber,
		@TransDate,
		@Plaza,
		@LaneGroup,
		@Gantry,
		@Lane,
		@PLAZA_ID,
		@Shift,
		@Class,
		@TollExp,
		@TollColl,
		@TollType,
		@Axel,
		@UFM_CALC
	)
	END TRY
	BEGIN CATCH
			DECLARE @ERR_SEV INT
			DECLARE @ERR_NUM INT
			DECLARE @ERR_LINE INT
			DECLARE @ERR_MSG NVARCHAR(MAX)

			SELECT
				@ERR_SEV    = ERROR_MESSAGE()
				,@ERR_NUM	= ERROR_NUMBER()
				,@ERR_LINE	= ERROR_SEVERITY()
				,@ERR_MSG 	= ERROR_LINE()

			INSERT INTO [dbo].[ErrTable]
				([FileType]
				,[ImportFileID]
				,[ImportFileLineNumber]
				,[SQL_ERROR]
				,[ErrDate]
				,ErrNumber
				,ErrSeverity
				,ErrLine)
			VALUES
				('1220'
				,@ImportFileID
				,@ImportFileLineNumber
				,@ERR_MSG
				,GetDate()
				,@ERR_NUM
				,@ERR_SEV
				,@ERR_LINE
				)
		IF(@ERR_SEV >= 17)
		BEGIN
			exec msdb.dbo.sp_send_dbmail
				@profile_name = 'DBMailer'
				,@recipients = 'ggoksu@gmx-way.com' 
				,@subject = 'Database Insert Error'
				,@body = @ERR_SEV
				,@body_format = 'HTML'

		END
	END CATCH
END
END
