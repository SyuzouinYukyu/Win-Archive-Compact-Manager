# API definitions and references

Definitions were read from installed Windows SDK 10.0.26100.0 headers: um/wofapi.h, um/winioctl.h, um/winbase.h, um/ioapiset.h and shared/winerror.h.

- WofIsExternalFile and WofSetFileDataLocation: WOF_FILE_COMPRESSION_INFO_V1 is two ULONGs; WOF_PROVIDER_FILE=2; XPRESS4K=0, LZX=1, XPRESS8K=2, XPRESS16K=3. [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/wofapi/nf-wofapi-wofsetfiledatalocation)
- FSCTL_SET_COMPRESSION=0x9C040, USHORT 0/1. FSCTL_DELETE_EXTERNAL_BACKING=0x90314. Opening a WOF file for write may transparently remove backing; the implementation rechecks the state after opening. [Microsoft](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/fsctl-delete-external-backing)
- FILE_STANDARD_INFO is queried for normal allocation. GetCompressedFileSizeW supplies NTFS/WOF compressed allocation. The normal uncompressed GetCompressedFileSize result is not used as allocation because it can equal logical length. [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew)
- CancelSynchronousIo requires THREAD_TERMINATE (0x1). Cancellation is a request, not proof of operation completion. [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelsynchronousio)
- FSCTL_QUERY_USN_JOURNAL=0x900F4; FSCTL_READ_USN_JOURNAL=0x900BB; READ_USN_JOURNAL_DATA_V1 offsets 0/8/12/16/24/32/40/42, native size 48. Version range is explicitly V2/V2; V2 record boundaries, alignment and name bounds are checked. FILE_ID_DESCRIPTOR native size 24, FileIdType=0. [Microsoft](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ni-ntifs-fsctl_read_usn_journal)
- IOCTL_STORAGE_QUERY_PROPERTY=0x2D1400; StorageDeviceProperty=0; StorageDeviceSeekPenaltyProperty=7; BusTypeNvme=17. DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty offset 8. [Microsoft](https://devblogs.microsoft.com/oldnewthing/20201023-00/?p=104395)
- SQLitePCLRaw.bundle_e_sqlite3 3.0.5 was selected after restore flagged the old native dependency. No audit warning is suppressed. [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5)

compact.exe was evaluated on isolated fixtures and rejected extended-path syntax; the final engine uses the documented APIs directly and does not spawn compact.exe.
