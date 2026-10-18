namespace KNE;

/// <summary>Finite numeric operations shared by code and generated graphs.</summary>
internal static class CalculationRuntimeLua
{
    public const string Source = """
        function kne_math_value(value, name)
            if type(value) ~= 'number' or value ~= value or value == math.huge or value == -math.huge then
                error('Вычисления: требуется конечное число' .. (name and ' («' .. name .. '»)' or ''), 2)
            end
            return value + 0.0
        end
        function kne_math_constant(name)
            local constants = {pi=math.pi, tau=2*math.pi, e=math.exp(1), phi=(1+math.sqrt(5))/2, half_pi=math.pi/2, sqrt2=math.sqrt(2)}
            return kne_math_value(constants[name], name)
        end
        function kne_math_op(op, ...)
            local args = {...}
            for i=1,select('#', ...) do args[i]=kne_math_value(args[i]) end
            local a,b,c,d,t = table.unpack(args)
            local value
            if op=='add' then value=a+b elseif op=='sub' then value=a-b elseif op=='mul' then value=a*b
            elseif op=='div' or op=='mod' then
                if b==0 then error('Вычисления: деление на ноль',2) end
                if op=='div' then value=a/b else value=a%b end
            elseif op=='pow' then value=a^b
            elseif op=='sqrt' then if a<0 then error('Вычисления: корень отрицательного числа',2) end; value=math.sqrt(a)
            elseif op=='sin' then value=math.sin(a) elseif op=='cos' then value=math.cos(a) elseif op=='tan' then value=math.tan(a)
            elseif op=='abs' then value=math.abs(a) elseif op=='floor' then value=math.floor(a) elseif op=='ceil' then value=math.ceil(a)
            elseif op=='round' then value=a>=0 and math.floor(a+0.5) or math.ceil(a-0.5)
            elseif op=='min' then value=math.min(a,b) elseif op=='max' then value=math.max(a,b)
            elseif op=='clamp' then if b>c then error('Вычисления: минимум больше максимума',2) end; value=math.max(b,math.min(c,a))
            elseif op=='lerp' then
                if c<0 or c>1 then error('Вычисления: коэффициент интерполяции должен быть от 0 до 1',2) end
                value=a*(1-c)+b*c
            elseif op=='remap' then
                if b==c then error('Вычисления: исходный диапазон не может иметь нулевую длину',2) end
                local numerator,denominator=a-b,c-b
                local factor
                if math.abs(numerator)==math.huge or math.abs(denominator)==math.huge then factor=(a/2-b/2)/(c/2-b/2)
                else factor=numerator/denominator end
                value=d*(1-factor)+t*factor
            else error('Неизвестная математическая операция: '..tostring(op),2) end
            return kne_math_value(value)
        end
        function kne_weighted_number(rows)
            if type(rows)~='table' or #rows<1 or #rows>100 then error('Таблица весов: от 1 до 100 строк',2) end
            local maximum,total=0,0.0
            for _,row in ipairs(rows) do
                kne_math_value(row[1]); kne_math_value(row[2])
                if row[2]<0 then error('Вес не может быть отрицательным',2) end
                maximum=math.max(maximum,row[2])
                total=total+row[2]
            end
            if maximum==0 then error('Нужен хотя бы один положительный вес',2) end
            local scale=1
            if total==math.huge then scale=maximum; total=0; for _,row in ipairs(rows) do total=total+row[2]/scale end end
            local value=math.random(); local accumulated=0; local last
            for _,row in ipairs(rows) do
                if row[2]>0 then last=row[1]; accumulated=accumulated+row[2]/scale; if value<accumulated/total then return row[1] end end
            end
            return last
        end
        """;
}
