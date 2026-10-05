/********************************************************************************
* Name		: uspr_trn_CPC_TransactionDetails_withETCNum
*
* Author:
*		Unknown
* Description:
*		
* Notes		:
*		Old Report #27
*		New Report #1220
*		uspr_trn --> Transaction Reports
*		Called from OPS..uspr_trn_TransactionDetails_sql
*
*	Arguments:	
*		@inOrgID The facility ID to look for (0 for ALL)
*		@GroupBy The field to Query Upon
* 			1 - Date 
*			2 - LaneNumber
*			3 - AttendantID
*			4 - ShiftID 
*			5 - Vault Number
*		@selectVal The number of the group type provided (0 for All)
*		@inStartDate The Beginning Date Range
*		@inEndDate The Ending Date Range
*
* Modifications:
* 	09/13/2004	YZ 	Original release
* 	02/11/2005	GSK 	Code refresh
**   	03/16/2005  RCW     Hotfix for Bridge Track Issue #2201    Selection of Partnum can only be done on CPC Machine
*	11/23/2005	RW	procRptTransactionDetails_sql Renamed
*	07/05/2006	YR	Get @PartNumStart from @inStartDate QC: 596. Make dates to min(StartDate) & max(enddate)
					Return VaultNo from tblVaultTourDuty for TransType ACM
*	08/10/2006	SN	TollDay Added	
*	08/28/2006	SN	ACM - Isnull added 1101 using this report		
*	02/12/2007	SN	If TransType =VIOL then VTOLL returned.
*	05/08/2007	SN	LaneGroup Changes.
*   05/10/2007     GM Changed TransDate to VARCHAR to get milisecond in Rpt-1220 for MDX	
*	03/13/2008	MF	Changed to RPrl schema
*	09/09/2008	KC	Changed group by and report by values to match what's in Pearl metadata.  Performance tuning.
*					Corrected where clause - added Parentheses around OR clauses to prevent unexpected results 
*	10/3/2008	KC	Modified group by section of the where clause to allow for NULL @selectVal
*	11/21/2008	RW	Modified FOr correct Report Type and Group By Parameter Valuses (1 -8 and 1 - 5 not 1,51,52,53,54,55,98,144 and 91,101,86,79,88)
*	04/30/2009	VSC	Modified for lost BT #2045 correction for determination of fare based on UOType and TransType values
*   05/15/2009  CAW Dropped the _sql suffix from this procedure and adjusted OPS dependencies accordingly. Adjusted time-setting logic
*					to support time-specific transaction selection.
*	10/27/2009	SN	AttendantID to be converted.
*	08/25/2011	GM	Assignment ID filteration for different orgs
*	08/25/2011	SN	AssignmentID for that particular Org used and performance improvements.
*   04/24/2014	RAM OrgID join with RPT.dbo.tblTransStore table. PPL-741_DB Ref : DB20140423-12
*   04/25/2014	RAM Input parameter @inOrgID length increased 20 to 255 and multi OrgID selection 
*					functionality added - PPL-741_DB Ref : DB20140425-13
*   12/04/2014	MD  Changed IF case to properly handle the Time Specific DayType
*	09/02/2016	MD	Changed to include TransponderID in the returned TransType field 
***********************************************************************************************************************************
* NOTICE OF COPYRIGHT AND PROPRIETARY INFORMATION
* This proprietary work product is protected both under trade secret laws and under the United States copyright laws. 
* It contains technical information which is proprietary to TransCore Holdings, Inc. The contents of this
* document may not be used, reproduced, distributed, or disclosed in whole or in part, except
* insofar as that use, reproduction, distribution, or disclosure is authorized in writing by TransCore Holdings, Inc.
*
* (c) 2013 TransCore Holdings, Inc.  All rights reserved.
***********************************************************************************************************************************/
--EXEC [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]	15,1,1,0,'07/01/2011', '07/15/2011',1,0
CREATE PROCEDURE [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum] 
	@inOrgID VARCHAR(255),
	@ReportType INT,
	@GroupBy INT ,
	@selectVal  VARCHAR(20) = NULL,
	@inStartDate DATETIME, --requested date
	@inEndDate DATETIME,
	@inDayType INT,
	@inLaneGroupID	VARCHAR(5)='0'
AS
SET NOCOUNT ON

BEGIN

	DECLARE  @PartNumStart INT,
		@PartNumEnd INT

	DECLARE @AttendantID BIGINT

	--this helps SQL prepare the best optimization plan, improves performance
	DECLARE 
		--@newOrgID BIGINT,
		@newReportType INT ,
		@newGroupBy BIGINT ,
		@newselectVal  VARCHAR(20) ,
		@newStartDate DATETIME, --requested date
		@newEndDate DATETIME,
		@newDayType INT,
		@newLaneGroupID	BIGINT
	
	DECLARE @OrgList TABLE	(
			OrgID BIGINT PRIMARY KEY,
			OrgName	VARCHAR(30)
	)

	DECLARE @OrgRowCount TINYINT
	SET @OrgRowCount = 0

	CREATE TABLE #TransactionTableMain(
		OrgID BIGINT,
		OrgName	VARCHAR(30),
		LaneNumber INT,
		ShiftID INT,
		TransDate DATETIME,	
		VehClass VARCHAR(3),
		VehAxlesExpected SMALLINT,
		VehAxlesCounted SMALLINT,
		FareExpected MONEY,
		FarePaid MONEY,
		TransType VARCHAR(4),
		AttendantID BIGINT,
		AssignmentID BIGINT,
		AssignmentType VARCHAR(10),
		TollDayStart DATETIME,
		UOTransID BIGINT,
		UOTypeID VARCHAR(3),
		UOTypeName VARCHAR(70),
		ViolationAxles INT,			
		UserName VARCHAR(100),
		BadgeNumber VARCHAR(20),
		ETCNumber VARCHAR(30),	
		FullToll MONEY
	)

	CREATE TABLE #TransactionTable(
		TransDate		DATETIME, 
		TransDateV		VARCHAR(25), 
		[Shift]			INT, 
		Lane			INT,  
		Attendant		VARCHAR(100), 
		BadgeNumber		INT,
		VehClass		VARCHAR(3), 
		TollFull		MONEY, 
		TollCharged		MONEY, 
		TollCollected	MONEY, 
		IVISAxles		INT, 
		CollectorAxles	INT, 
		TransType		VARCHAR(100),
		OrgName			VARCHAR(30),
		GroupData		INT,
		TollDay			DATETIME,
		LaneGroupID		INT,
		LaneGroupName	VARCHAR(60),
		AssignmentID	BIGINT,
		OrgID			BIGINT
	
	)

	CREATE INDEX IX_TMP_TransDate		ON #TransactionTable(TransDate)
	CREATE INDEX IX_TMP_OrgID			ON #TransactionTable(OrgID)
	CREATE INDEX IX_TMP_AssignmentID	ON #TransactionTable(AssignmentID)

	--SET 	@newOrgID = @inOrgID
	SET 	@newReportType = @ReportType
	SET 	@newGroupBy  = @GroupBy
	SET 	@newselectVal  = ISNULL(@selectVal,'0')

	IF @newselectVal = ''
		SET 	@newselectVal  = '0'


	IF @newGroupBy =3 
		BEGIN
			SELECT 
				TOP 1 @AttendantID= UserID 
			FROM 
				[CPC].[Sec].cfgUser (NOLOCK)
			WHERE 
				LoginUserID = @newselectVal 
				AND IsActive=1
		END		

	INSERT @OrgList(
			OrgID,
			OrgName
		)
		SELECT 
			OrgID,
			OrgName
		FROM 
			[ReportLayer].[Prl].[udf_prl_OrgTable] (@inOrgID)

	SET @OrgRowCount=@@ROWCOUNT

	--set 	@newStartDate = @inStartDate
	--set 	@newEndDate =@inEndDate
	--set 	@newDayType = @inDayType

	-- CAW (5/15/2009): Changed date setting logic to support the time-specific selection option.
	-- 1 = Toll Day, 2 = Calendar Day, 3 = Time Specific (use start date, end date)
	/*IF (@inDayType = 1 OR @inDayType = 2)
		BEGIN
			SET 	@newDayType = @inDayType 
			SELECT 	@newStartDate = MIN(StartDate), 
					@newEndDate = MAX(EndDate)
					--,@newOrgID = COALESCE(@newOrgID,0)
			FROM	[CPC].[dbo].udf_TollDay_Or_CalendarDay (@newDayType, @inStartDate, @inEndDate)
		END
	ELSE
		BEGIN
			SET @newStartDate = @inStartDate
			SET @newEndDate = @inEndDate
		END
	*/
	SET 	@newDayType = @inDayType 
	IF @newDayType = 3 
		BEGIN
			SET @newStartDate = @inStartDate
			SET @newEndDate = @inEndDate
		END
	ELSE 
		BEGIN
			IF @OrgRowCount=1
				BEGIN
					SELECT 	
						@newStartDate = StartDate,
						@newEndDate = EndDate		
					FROM 	[CPC].[RPrl].udf_TollDay_Or_CalendarDay(        
						@newDayType,
						@inStartDate,
						@inEndDate) A
					INNER JOIN @OrgList B 
					ON A.OrgID = B.OrgID
				END
			ELSE IF @OrgRowCount>0
				BEGIN
					SELECT TOP 1
						@newStartDate = StartDate,
						@newEndDate = EndDate		
					FROM 	[CPC].[RPrl].udf_TollDay_Or_CalendarDay(
						@newDayType,
						@inStartDate,
						@inEndDate)
				END
		END
	SET 	@newLaneGroupID	= @inLaneGroupID

	--SELECT top 1	@newStartDate = MIN(StartDate) , 
	--	@newEndDate = MAX(EndDate) ,
	--	@newOrgID = COALESCE(@newOrgID,0)
	--From 	udf_TollDay_Or_CalendarDay (@newDayType,@newStartDate,@newEndDate)

	--SELECT	@PartNumStart = RPT.dbo.[udf_Date2PartNum](@inEndDate) ,


	SELECT	@PartNumStart = [RPT].[dbo].[udf_Date2PartNum](@newStartDate) ,
		@PartNumEnd = [RPT].[dbo].[udf_Date2PartNum](@newEndDate)

	--SET @newOrgID = ISNULL(@newOrgID,0)
	

	/*SELECT
		OrgID,
		OrgName
	FROM
		CPC.dbo.tblOrg (NOLOCK)
	WHERE
		 IsActive=1
		 AND
		(OrgID = @newOrgID  OR @newOrgID=0)*/
	---------------------------------
	INSERT INTO #TransactionTableMain(
			OrgID,
			OrgName,	
			LaneNumber,
			ShiftID,
			TransDate,	
			VehClass,
			VehAxlesExpected,
			VehAxlesCounted,
			FareExpected,
			FarePaid,
			TransType,
			AttendantID,
			AssignmentID,
			AssignmentType,
			TollDayStart,
			UOTransID,
			UOTypeID,
			UOTypeName,
			ViolationAxles,			
			UserName,
			BadgeNumber,
			ETCNumber,	
			FullToll
		)						
	SELECT
			TT.OrgID,
			O.OrgName,
			LaneNumber,
			ShiftID,
			TransDate,	
			VehClass,
			VehAxlesExpected,
			VehAxlesCounted,
			FareExpected,
			FarePaid,
			TransType,
			AttendantID,
			AssignmentID,
			AssignmentType,
			TollDayStart,
			UOTransID,
			UOTypeID,
			UOTypeName,
			ViolationAxles,			
			UserName,
			BadgeNumber,
			ETCNumber,	
			FullToll
	FROM
			[RPT].[dbo].tblTransStore TT WITH(NOLOCK) INNER JOIN
				@OrgList O ON O.OrgID=TT.OrgID 
	WHERE
			PartNum		BETWEEN @PartNumStart AND @PartNumEnd  AND	
			TransDate	BETWEEN @newStartDate AND @newEndDate 	
	------------------
	--SELECT * FROM #TransactionTableMain
	--RETURN								

	------------------------------------

	INSERT INTO #TransactionTable(
		TransDate,
		TransDateV,
		Shift , 
		Lane ,  
		Attendant , 
		BadgeNumber ,
		VehClass , 
		TollFull , 
		TollCharged , 
		TollCollected , 
		IVISAxles , 
		CollectorAxles , 
		TransType,
		OrgName,
		GroupData,
		TollDay,
		LaneGroupID,
		LaneGroupName,
		AssignmentID,
		OrgID
	)
	SELECT 	
		TransDate,
		CONVERT(VARCHAR(25), TransDate, 121),
		ShiftID,
		TC.LaneNumber,
		UserName,
		CASE TransType 
			WHEN 'ACM' THEN CAST(AssignmentID AS INT)
			WHEN 'GRND' THEN CAST(AssignmentID AS INT)
			ELSE BadgeNumber 
		END,
		VehClass,
		FullToll,
		CASE 	
			WHEN UOTypeID = '003' AND FullToll = 0 THEN FareExpected
			WHEN UOTypeID IN ('013', '014', '016', '017', '018', '019', '020', '021', '022') THEN 0
			WHEN LEFT(TransType, 3) = 'ETC' AND AssignmentType = 'EXTNL NR'    THEN 0
			WHEN LEFT(TransType, 3) = 'ETC' THEN FareExpected
			ELSE FullToll -- BT#2045 
		END AS TollCharged,
		CASE 
			WHEN UOTypeID IN ('002','013', '014', '016', '017', '018', '019', '020', '021', '022') THEN 0
			WHEN LEFT(TransType, 3) = 'ETC' AND AssignmentType = 'EXTNL NR'    THEN 0
			ELSE FarePaid 
		END AS TollCollected,
		CASE 
			WHEN UOTransID <> 0 THEN ViolationAxles 
			ELSE VehAxlesCounted 
		END AS IVISAxles,
		VehAxlesExpected,
		CASE 	
			WHEN TransType = 'EVNT' THEN 'Special Events Mode'
			WHEN  LEFT(TransType,3) =  'ETC' AND AssignmentType = 'EXTNL NR'    THEN 'ETC-Non Revenue'
			WHEN  LEFT(TransType,3) =  'ETC' AND UOTypeID IN ('004','005','006', '007','015')     THEN 'ETC-' + UOTypeName
			WHEN UOTypeID > 0  THEN TransType + '-' + UOTypeName + ' ' + ISNULL(ETCNumber,'')
			WHEN TransType = 'GRND' THEN 'ACM-Ground Money'
			WHEN TransType = 'PDMP' THEN 'Cash-Prepay Remaining Cash'
			WHEN LEFT(TransType,3) = 'ETC'  THEN 'ETC-' + ETCNumber
			WHEN TransType = 'ACM'  THEN 'ACM-Vault 0'/*    ISNULL((SELECT TOP 1 CONVERT(VARCHAR,ISNULL(VT.VaultNo,0)) FROM CPC.dbo.tblVaultTourDuty VT (NOLOCK) WHERE VT.VaultAssignID = TC.AssignmentID AND VT.OrgID = TC.OrgID),0)*/
			WHEN TransType = 'CASH' THEN 'CASH-Paid'
			WHEN TransType = 'ADJ' THEN 'Segment Added'
			ELSE TransType
		END,
		TC.OrgName, 	
		CASE @newGroupBy 
			WHEN 0 THEN CAST(TC.OrgID AS INT)   --used to be 1, all
			WHEN 1 THEN CAST(TC.OrgID AS INT)   --used to be 1, all
			WHEN 2 THEN TC.LaneNumber   --used to be 2, lane
			WHEN 3 THEN CAST(BadgeNumber AS INT)  --used to be 3, attendant
			WHEN 4 THEN ShiftID --used to be 4, shift
			WHEN 5 THEN CAST(AssignmentID AS INT) --used to be 5, vault
		END,
		TollDayStart AS TollDay,
		LG.LaneGroupID,
		LG.LaneGroupName,
		CASE 
			 WHEN TransType = 'ACM'
			 THEN AssignmentID
			 ELSE 0
		 END,
		 TC.OrgID
	FROM   
		#TransactionTableMain TC  INNER JOIN 	
		[CPC].[RPrl].[udfr_CPC_LaneGroup](@newLaneGroupID) LG ON 
			TC.OrgID = LG.OrgID AND TC.LaneNumber = LG.LaneNumber
	WHERE  	
		 (
			@newReportType IN (0,1,6)   --used to be IN (1,6) 
			OR 
			(@newReportType = 2 AND (LEFT(TransType, 3) = 'ETC' OR AssignmentType = 'EXTNL UO'))  --used to be 2  
			OR 
			(@newReportType = 3 AND TransType IN ('CASH', 'PDMP', 'EVNT','ADJ'))  --used to be 3, Cash
			OR 
			(@newReportType = 4 AND TransType IN ('ACM', 'GRND'))  --used to be 4, ACM
			OR 
			(@newReportType = 5 AND VehAxlesExpected <> --used to be 5, Axle Variance
							CASE 
							WHEN ViolationAxles > 0 AND VehAxlesExpected <> ViolationAxles THEN ViolationAxles 
							ELSE VehAxlesCounted 
							END)
			OR (@newReportType = 7 AND UOTypeID > 0 ) --used to be 7, UO
			OR (@newReportType = 8 AND TransType = 'EVNT' ) --used to be 8, Event Mode--loop variance???
			)	
		AND (
			@newGroupBy IN (0,1)  --used to be 1, all
			OR 
			@newselectVal = '0'
			OR 
			(@newGroupBy = 2 AND ( @newselectVal = '0' OR TC.LaneNumber = @newselectVal)) --used to be 2, lane
			OR 
			(@newGroupBy = 3 AND ( @newselectVal = '0' OR AttendantID  = @AttendantID))  --used to be 3, attendant
			OR 
			(@newGroupBy = 4 AND (@newselectVal = '0' OR ShiftID = @newselectVal))  --used to be 4, Shift
			OR 
			(@newGroupBy = 5 AND (@newselectVal = '0'  OR (AssignmentID = @newselectVal AND AssignmentType = 'VAULT')))  --used to be 5, vault
			)
	--ORDER BY TC.TransDate

	DROP TABLE #TransactionTableMain

	UPDATE 
		TMP
	SET
		TransType =  'ACM-Vault'+CONVERT(VARCHAR,ISNULL(VT.VaultNo,0))
	FROM 
		#TransactionTable TMP
		INNER JOIN  [CPC].[dbo].tblVaultTourDuty VT 
		ON TMP.AssignmentID = VT.VaultAssignID	
			AND TMP.OrgID = VT.OrgID
	WHERE	
		TMP.AssignmentID > 0
	
	

	IF NOT EXISTS(SELECT 1 FROM #TransactionTable)
		INSERT INTO #TransactionTable
			(OrgName,
			TransDateV,
			GroupData,
			LaneGroupID,
			LaneGroupName)
		SELECT 	OrgName,
			@newStartDate,
			@newGroupBy,
			0,
			'N/A'
		FROM	
			@OrgList  
	
	SELECT 
		TransDate,
		TransDateV,
		Shift , 
		Lane ,  
		Attendant , 
		BadgeNumber ,
		VehClass , 
		TollFull , 
		TollCharged , 
		TollCollected , 
		IVISAxles , 
		CollectorAxles , 
		TransType,
		OrgName,
		GroupData,
		TollDay,
		LaneGroupID,
		LaneGroupName
	FROM 
		#TransactionTable 
	ORDER BY TransDate



	DROP TABLE #TransactionTable

	RETURN

END
