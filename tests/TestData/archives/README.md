# Archive fixtures

Data only. Never executed. Used by `ArchiveEngineCapabilityTests`.

Source tree for the generated fixtures: `a.txt` ("hello secret one\n") and `sub/b.txt` ("second file contents 12345\n").

## Generated with 7-Zip (7z.exe) on Windows

Password for every encrypted one: `EzTest-123!` (a test value).

| Folder | Command |
|---|---|
| zip-zipcrypto | `7z a -tzip -p<pw> -mem=ZipCrypto sample.zip a.txt sub` |
| zip-aes256 | `7z a -tzip -p<pw> -mem=AES256 sample.zip a.txt sub` |
| 7z-aes-plainhdr | `7z a -t7z -p<pw> sample.7z a.txt sub` (file names visible) |
| 7z-aes-enchdr | `7z a -t7z -p<pw> -mhe=on sample.7z a.txt sub` (headers encrypted) |
| 7z-plain | `7z a -t7z sample.7z a.txt sub` |
| tar-gz | `7z a -ttar x.tar a.txt sub` then `7z a -tgzip sample.tar.gz x.tar` |
| zip-ntfs-times | `7z a -tzip -mtc=on -mta=on sample.zip a.txt sub` (NTFS 0x000a extra field) |
| 7z-all-times | `7z a -t7z -mtc=on -mta=on sample.7z a.txt sub` (stores ctime/atime) |

## RAR (cannot be created without WinRAR; downloaded)

Source: https://github.com/adamhathcock/sharpcompress, `tests/TestArchives/Archives/` (MIT License, Copyright (c) 2014 Adam Hathcock).
Password: `test` (from that repo's RarArchiveTests). Contents: `exe\test.exe`, `jpg\test.jpg`, `тест.txt`.

| Folder | File | Kind |
|---|---|---|
| rar-Rar.encrypted_filesAndHeader | Rar.encrypted_filesAndHeader.rar | RAR4, `-hp` (headers + files) |
| rar-Rar5.encrypted_filesAndHeader | Rar5.encrypted_filesAndHeader.rar | RAR5, `-hp` |
| rar-Rar5.encrypted_filesOnly | Rar5.encrypted_filesOnly.rar | RAR5, `-p` (names visible) |
