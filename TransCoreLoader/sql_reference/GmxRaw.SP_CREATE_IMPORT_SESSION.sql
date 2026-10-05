-- Create date: 2/8/2024
-- Description:	Create a sesssion
-- =============================================
CREATE PROCEDURE [dbo].[SP_CREATE_IMPORT_SESSION] 
	-- Add the parameters for the stored procedure here
	@SESSION_HOST nvarchar(256),
	@SESSION_USER nvarchar(256)

AS
BEGIN
	SET NOCOUNT ON;
    DECLARE @IDs TABLE(ID BIGINT);

	DECLARE @SESSION_ID bigint
	EXEC SP_GET_SEQ @SEQ_NAME = 'IMPORT_SESSION' , @SEQ_NO_OUT = @SESSION_ID OUTPUT

	INSERT INTO [dbo].[ImportSession]
	           ([SessionID]
	           ,[SessionStart]
	           ,[SessionHost]
	           ,[SessionUser])
    OUTPUT 
		inserted.ID INTO @IDs(ID)
	VALUES
	           (@SESSION_ID
	           ,GETDATE()
	           ,@SESSION_HOST
			   ,@SESSION_USER
			   )

--	SELECT ID FROM @IDs

	RETURN @SESSION_ID
END
