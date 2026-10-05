-- Create date: 2/8/2024
-- Description:	Get A Sequence Number
-- =============================================
CREATE PROCEDURE SP_GET_SEQ 
	-- Add the parameters for the stored procedure here
	@SEQ_NAME nvarchar(256), 
	@SEQ_NO_OUT bigint OUTPUT
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;
	DECLARE @SEQ_NO_DB bigint
	SELECT 
		@SEQ_NO_DB = SEQ_NO
	FROM
		SequenceNo (nolock)
	WHERE
		SEQ_NAME = @SEQ_NAME
	IF(ISNULL(@SEQ_NO_DB,0) = 0)
	BEGIN
		SELECT @SEQ_NO_OUT = 1
		INSERT INTO SequenceNo (SEQ_NAME,SEQ_NO) Values (@SEQ_NAME,1)
	END
	ELSE
	BEGIN
		SELECT @SEQ_NO_OUT = @SEQ_NO_DB+1
		UPDATE SequenceNo SET SEQ_NO = @SEQ_NO_DB+1 WHERE SEQ_NAME = @SEQ_NAME 
	END
END
