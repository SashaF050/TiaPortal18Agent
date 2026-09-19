param(
    [string]$Command = "list-processes",
    [string]$Arg1,
    [string]$Arg2
)
$exe = "C:\Users\aa.fedin\Desktop\Новая папка\Тест\Мечта 1\Tia18Agent\TiaPortal18Agent.exe"
if ($Arg2) {
    & $exe $Command $Arg1 $Arg2
} elseif ($Arg1) {
    & $exe $Command $Arg1
} else {
    & $exe $Command
}
