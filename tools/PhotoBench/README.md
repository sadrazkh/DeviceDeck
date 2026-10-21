# PhotoBench

Times DeviceDeck's photo backup: the real `PhotoBackupService` through the transfer queue, against a connected
iPhone or a fake one. A developer tool; not shipped, not part of the release.

```
dotnet run --project tools/PhotoBench -c Release -- --since 30d --dest E:\DeviceDeck-test\bench-tip
dotnet run --project tools/PhotoBench -c Release -- --fake --repeat 3
```

It prints how many photos are new, then the backup's photos per minute, MB/s and how many usbmux connections it
opened (each lockdown session and each service connection is one), then looks again (it should find 0 new).

## A real iPhone

- Unlocked, and already trusted by this PC (pair it in DeviceDeck first: the bench never asks the phone to trust).
- `--since` is required (`30d`, `2026-09-01` or `all`), so a whole library is never copied by accident.
- It only reads from the phone. Copies go into `--dest`; the ledger, the queue and the Photos.sqlite copy go into
  `<dest>.photobench` next to it. Nothing is deleted. `--dest` on C: is refused.
- Running it again into the same folder starts from that ledger: it must find 0 new photos.
- `--edits` also says, read-only, how many photos Photos.sqlite marks as edited and whether their rendered versions
  are where the backup looks (`PhotoData/Mutations/<DCIM path>/Adjustments/FullSizeRender.*`); `--save-edited` backs
  them up too, as the panel's "Also save edited versions".
- Quit DeviceDeck first only if the phone misbehaves with two clients; usbmux itself allows both.

## Fake mode

`--fake` builds an iPhone from the iPhone tests' fakes (usbmuxd, lockdown, AFC) with a made-up camera roll (default
240 photos and videos over the last 60 days: HEIC with and without Live Photo videos, JPG, PNG screenshots, a few
long videos; about 1 GB) and runs it over a simulated USB link:

| Option | Default | Models |
| --- | --- | --- |
| `--mbps` | 34 | the link's bandwidth, shared by every connection (an iPhone 16 pulls 33–35 MB/s) |
| `--rtt-ms` | 1 | a request's round trip |
| `--connect-ms` | 5 | opening a connection to usbmuxd |
| `--session-ms` | 40 | a lockdown session's TLS handshake |

The copies go to `E:\DeviceDeck-test\photobench-fake` and are removed after each run (`--keep` keeps them).
`--repeat N` runs N times, each into a fresh folder, and prints the median.
