using DalamudMCP.Tools;
var checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS: "+name);}
var bytes=new byte[60*16];
Check(StatusSlotReader.Read(bytes).Count==0,"empty native array contains no active statuses");
void Write(int index,ushort id,ushort param,float remaining,ulong source){var offset=index*16;BitConverter.GetBytes(id).CopyTo(bytes,offset);BitConverter.GetBytes(param).CopyTo(bytes,offset+2);BitConverter.GetBytes(remaining).CopyTo(bytes,offset+4);BitConverter.GetBytes(source).CopyTo(bytes,offset+8);}
Write(0,50,0,20,0x100000001);Write(59,297,7,3.5f,0x123456789ABCDEF0);
var slots=StatusSlotReader.Read(bytes);
Check(slots.Count==2,"sparse slots do not produce empty entries");
Check(slots[1].Index==59&&slots[1].Id==297,"last slot remains visible despite a gap");
Check(slots[1].Source==0x123456789ABCDEF0,"source object id preserves all 64 bits");
Check(slots[1].Param==7&&slots[1].Remaining==3.5f,"parameters and duration preserved");
Write(0,0,0,0,0);
Check(StatusSlotReader.Read(bytes).Single().Index==59,"removing an early status cannot hide later active slots");
bool refused=false;try{StatusSlotReader.Read(new byte[16]);}catch(ArgumentException){refused=true;}
Check(refused,"incomplete native arrays refused");
Console.WriteLine($"{checks}/{checks} checks passed");
