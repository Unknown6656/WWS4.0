CREATE TABLE [dbo].[__WWS__RemoteHosts] (
    [ID]        BIGINT        NOT NULL,
    [IPAddress] VARCHAR (64)  DEFAULT ('[::]') NOT NULL,
    [UserAgent] NVARCHAR (512) DEFAULT ('') NOT NULL,
    PRIMARY KEY CLUSTERED ([ID] ASC),
    CONSTRAINT [RemoteHost] UNIQUE NONCLUSTERED ([IPAddress] ASC, [UserAgent] ASC)
);

