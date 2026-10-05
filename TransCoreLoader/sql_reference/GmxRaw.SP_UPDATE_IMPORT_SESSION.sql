-- Create date: 2/8/2024
-- Description:	Update End of a session
-- =============================================
CREATE PROCEDURE SP_UPDATE_SESSION 
	-- Add the parameters for the stored procedure here
	@SESSION_ID bigint = 0, 
	@NOF_FILES int = 0
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;
UPDATE [dbo].[ImportSession]
   SET 
      [SessionEnd] = GETDATE()
      ,[FilesProcessed] = @NOF_FILES
 WHERE 
	[SessionID] = @SESSION_ID

END
