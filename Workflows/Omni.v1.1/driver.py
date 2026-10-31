"""Submit exactly one prepared graph; measure full-job GPU and Windows RAM."""
from pathlib import Path
import json, time, urllib.request, urllib.error, uuid, subprocess, threading, statistics
import psutil, sys, os

JOB="Run01"
RESULT = Path(os.environ["TMT_R08_JOB"]).resolve()
URL = "http://127.0.0.1:8199"

def api(path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(URL + path, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return json.load(response)
    except urllib.error.HTTPError as e:
        raise RuntimeError(e.read().decode("utf-8")) from e

queue = api("/queue")
assert not queue["queue_running"] and not queue["queue_pending"], "Diagnostic queue is not empty"
assert not (RESULT / "Submission.json").exists(), "A job has already been submitted; do not duplicate it"
system = api("/system_stats")
(RESULT / "System.json").write_text(json.dumps(system, indent=2), encoding="utf-8")
server_pid = int((RESULT / "server.pid").read_text().strip())
process = psutil.Process(server_pid)
assert "server.py" in " ".join(process.cmdline())
graph = json.loads((RESULT / "Prepared.api.json").read_text(encoding="utf-8"))
workflow = json.loads((RESULT / "Workflow.ui.json").read_text(encoding="utf-8"))
(RESULT / "Prompt.api.json").write_text(json.dumps(graph, indent=2), encoding="utf-8")
(RESULT / "Workflow.ui.json").write_text(json.dumps(workflow, ensure_ascii=False, indent=2), encoding="utf-8")
stopping = threading.Event()
samples = []

def monitor():
    with (RESULT / "Telemetry.jsonl").open("w", encoding="utf-8") as log:
        while not stopping.is_set():
            row = {"epoch": time.time()}
            try:
                m = process.memory_info()
                row.update(process_rss_bytes=m.rss, process_vms_bytes=m.vms,
                           process_private_bytes=getattr(m, "private", None))
                vm = psutil.virtual_memory()
                row.update(system_ram_used_bytes=vm.used, system_ram_available_bytes=vm.available)
                output = subprocess.check_output(["nvidia-smi", "--query-gpu=memory.total,memory.used,utilization.gpu,temperature.gpu,power.draw",
                    "--format=csv,noheader,nounits"], creationflags=0x08000000, timeout=8, text=True).strip().splitlines()[0]
                total, used, util, temp, power = [float(v.strip()) for v in output.split(",")]
                row.update(gpu_total_MiB=total, gpu_used_MiB=used, gpu_util_percent=util,
                           gpu_temperature_C=temp, gpu_power_W=power)
            except Exception as e:
                row["error"] = type(e).__name__ + ": " + str(e)
            samples.append(row)
            log.write(json.dumps(row) + "\n")
            log.flush()
            stopping.wait(1.0)

thread = threading.Thread(target=monitor, daemon=True)
thread.start()
while len(samples) < 3:
    time.sleep(0.25)
started = time.perf_counter()
record = {"client_id": "tmt-omni-v1-" + JOB + "-" + str(uuid.uuid4()), "submitted_epoch": time.time(),
          "server_pid": server_pid, "status": "submission_intent", "submissions": 1}
if os.environ.get("TMT_R08_MODE") != "ui":
    (RESULT / "Submission.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
try:
    if os.environ.get('TMT_R08_MODE') == 'ui':
        print('AWAITING ONE UI RUN; 300-second UI wait limit', flush=True)
        while True:
            path=RESULT/'Submission.json'
            if path.exists():
                record=json.loads(path.read_text(encoding='utf-8'))
                if 'response' in record:
                    response=record['response'];break
            if time.perf_counter()-started>300:raise TimeoutError('UI did not enqueue within 300s')
            time.sleep(.5)
        started=time.perf_counter()
    else:
        response = api("/prompt", {"prompt": graph, "client_id": record["client_id"],
            "extra_data": {"extra_pnginfo": {"workflow": workflow}}})
        record.update(response=response, status="submitted")
        (RESULT / "Submission.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    prompt_id = response["prompt_id"]
    print("QUEUED ONE JOB " + prompt_id, flush=True)
    last_report = time.monotonic()
    while True:
        history = api("/history/" + prompt_id)
        if prompt_id in history:
            item = history[prompt_id]
            record.update(client_wall_seconds=time.perf_counter() - started, ended_epoch=time.time(),
                          status=item.get("status", {}))
            (RESULT / "History.json").write_text(json.dumps(item, indent=2), encoding="utf-8")
            (RESULT / "Submission.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
            print("JOB FINISHED " + json.dumps(record), flush=True)
            break
        if time.monotonic() - last_report >= 25:
            runtime = json.loads((RESULT / "Runtime.json").read_text(encoding="utf-8"))
            active = [r for r in runtime["nodes"] if "seconds" not in r]
            print("RUNNING %.1fs node=%s H3=%d GPU_MiB=%s" % (time.perf_counter()-started,
                active[-1]["class"] if active else "unknown", len(runtime["h3_forwards"]), samples[-1].get("gpu_used_MiB")), flush=True)
            last_report = time.monotonic()
        if time.perf_counter() - started > 480:
            raise TimeoutError("Generation exceeded 480 seconds; stop owned server, no resubmission")
        time.sleep(1)
    time.sleep(2)
except BaseException as error:
    failure = dict(record, status="driver_failed", error=type(error).__name__ + ": " + str(error),
                   ended_epoch=time.time(), client_wall_seconds=time.perf_counter()-started)
    (RESULT / "Failure.json").write_text(json.dumps(failure, indent=2), encoding="utf-8")
    try:
        api("/interrupt", {})
    except Exception:
        pass
    raise
finally:
    stopping.set()
    thread.join(timeout=10)
    def peak(key):
        values = [r[key] for r in samples if isinstance(r.get(key), (int, float))]
        return max(values) if values else None
    baseline = [r["gpu_used_MiB"] for r in samples[:3] if "gpu_used_MiB" in r]
    summary = {"sample_count": len(samples), "sampling_interval_seconds": "approximately 1s plus nvidia-smi latency",
        "device_gpu_baseline_MiB": statistics.median(baseline) if baseline else None,
        "device_gpu_peak_MiB": peak("gpu_used_MiB"), "process_rss_peak_bytes": peak("process_rss_bytes"),
        "process_private_peak_bytes": peak("process_private_bytes"), "system_ram_used_peak_bytes": peak("system_ram_used_bytes"),
        "gpu_temperature_peak_C": peak("gpu_temperature_C"), "gpu_power_peak_W": peak("gpu_power_W"),
        "scope": "GPU memory is device-wide, includes other applications; RAM RSS is server working set, not all committed memory"}
    (RESULT / "Telemetry.summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("TELEMETRY " + json.dumps(summary), flush=True)
if record.get("status", {}).get("status_str") != "success":
    raise SystemExit("The one job did not succeed; inspect History.json")
