CREATE TABLE [dbo].[__WWS__Connections] (
    [ID]           BIGINT          NOT NULL,
    [HostID]       BIGINT          DEFAULT ((0)) NOT NULL,
    [TimestampUTC] DATETIME        DEFAULT ((0)) NOT NULL,
    [RequestedURI] NVARCHAR (2048) DEFAULT ('') NOT NULL,
    [OriginalURI]  NVARCHAR (2048) DEFAULT ('') NOT NULL,
    [Cookies]      NVARCHAR (2048) DEFAULT ('') NOT NULL,
    [HTTPMethod]   VARCHAR (16)    DEFAULT ('GET') NOT NULL,
    [StatusCode]   INT             NULL,
	[ReturnLength] BIGINT			 DEFAULT ((0)) NOT NULL,
    [RemotePort]   INT             DEFAULT (0xFFFE) NOT NULL,
    PRIMARY KEY CLUSTERED ([ID] ASC),
    FOREIGN KEY ([HostID]) REFERENCES [dbo].[__WWS__RemoteHosts] ([ID])
);

